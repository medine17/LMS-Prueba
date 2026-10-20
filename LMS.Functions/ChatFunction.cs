using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LMS.Functions;

public class ChatFunction
{
    private static readonly HttpClient HttpClient = new();

    private readonly TokenCredential _credential;
    private readonly Uri _agentEndpoint;
    private readonly ILogger<ChatFunction> _logger;

    public ChatFunction(
        IConfiguration configuration,
        ILogger<ChatFunction> logger)
    {
        _logger = logger;

        var projectEndpoint =
            configuration["AZURE_AI_PROJECT_ENDPOINT"]
            ?? throw new InvalidOperationException(
                "Falta configurar AZURE_AI_PROJECT_ENDPOINT.");

        var agentName =
            configuration["AZURE_AI_AGENT_NAME"]
            ?? throw new InvalidOperationException(
                "Falta configurar AZURE_AI_AGENT_NAME.");

        var endpoint = projectEndpoint.TrimEnd('/');
        var encodedAgentName = Uri.EscapeDataString(agentName);

        _agentEndpoint = new Uri(
            $"{endpoint}/agents/{encodedAgentName}" +
            "/endpoint/protocols/openai/responses?api-version=v1");

        _credential = new DefaultAzureCredential();
    }

    [Function("Chat")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(
            AuthorizationLevel.Function,
            "post",
            Route = "chat")]
        HttpRequestData request)
    {
        string? message;

        try
        {
            using var document =
                await JsonDocument.ParseAsync(request.Body);

            if (!document.RootElement.TryGetProperty(
                    "message", out var messageElement)
                || messageElement.ValueKind != JsonValueKind.String)
            {
                var badRequest =
                    request.CreateResponse(HttpStatusCode.BadRequest);

                await badRequest.WriteAsJsonAsync(new
                {
                    error = "Envía un JSON con la propiedad message de tipo texto."
                });

                return badRequest;
            }

            message = messageElement.GetString()?.Trim();
        }
        catch (JsonException)
        {
            var badRequest =
                request.CreateResponse(HttpStatusCode.BadRequest);

            await badRequest.WriteAsJsonAsync(new
            {
                error = "El cuerpo de la solicitud no contiene JSON válido."
            });

            return badRequest;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            var badRequest =
                request.CreateResponse(HttpStatusCode.BadRequest);

            await badRequest.WriteAsJsonAsync(new
            {
                error = "El mensaje no puede estar vacío."
            });

            return badRequest;
        }

        try
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(
                    new[] { "https://ai.azure.com/.default" }),
                CancellationToken.None);
            using var outboundRequest = new HttpRequestMessage(
                HttpMethod.Post,
                _agentEndpoint);

            outboundRequest.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    token.Token);

            outboundRequest.Content = JsonContent.Create(new
            {
                input = message,
                stream = false
            });

            using var response = await HttpClient.SendAsync(
                outboundRequest);

            var responseBody =
                await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "El endpoint del agente devolvió HTTP {StatusCode}. " +
                    "Reason={Reason}; ResponseBody={ResponseBody}",
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    Truncate(responseBody, 2000));

                return await CreateErrorResponse(request);
            }

            string answer;

            try
            {
                using var responseDocument =
                    JsonDocument.Parse(responseBody);

                answer = ExtractOutputText(
                    responseDocument.RootElement);
            }
            catch (JsonException ex)
            {
                _logger.LogError(
                    ex,
                    "La respuesta del agente no contiene JSON válido.");

                return await CreateErrorResponse(request);
            }

            if (string.IsNullOrWhiteSpace(answer))
            {
                _logger.LogError(
                    "El agente respondió correctamente, pero no se encontró texto en output.");

                return await CreateErrorResponse(request);
            }

            var result =
                request.CreateResponse(HttpStatusCode.OK);

            await result.WriteAsJsonAsync(new { answer });

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error al invocar el endpoint de Azure AI Foundry.");

            return await CreateErrorResponse(request);
        }
    }

    private static string ExtractOutputText(JsonElement root)
    {
        var textParts = new List<string>();

        if (root.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty(
                        "type", out var type)
                    && type.GetString() == "output_text"
                    && contentItem.TryGetProperty(
                        "text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    textParts.Add(text.GetString() ?? string.Empty);
                }
            }
        }

        return string.Join(Environment.NewLine, textParts);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private static async Task<HttpResponseData> CreateErrorResponse(
        HttpRequestData request)
    {
        var response =
            request.CreateResponse(HttpStatusCode.InternalServerError);

        await response.WriteAsJsonAsync(new
        {
            error = "No fue posible procesar el mensaje con el agente."
        });

        return response;
    }
}