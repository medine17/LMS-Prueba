using System.Net;
using System.Text.Json;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LMS.Functions;

public class ChatFunction
{
    private readonly ProjectResponsesClient _responsesClient;
    private readonly ILogger<ChatFunction> _logger;

    public ChatFunction(
        IConfiguration configuration,
        ILogger<ChatFunction> logger)
    {
        _logger = logger;

        var endpoint = configuration["AZURE_AI_PROJECT_ENDPOINT"]
            ?? throw new InvalidOperationException(
                "Falta configurar AZURE_AI_PROJECT_ENDPOINT.");

        var agentName = configuration["AZURE_AI_AGENT_NAME"]
            ?? throw new InvalidOperationException(
                "Falta configurar AZURE_AI_AGENT_NAME.");

        var projectClient = new AIProjectClient(
            endpoint: new Uri(endpoint),
            tokenProvider: new DefaultAzureCredential());

        _responsesClient = projectClient.ProjectOpenAIClient
            .GetProjectResponsesClientForAgent(agentName);
    }

    [Function("Chat")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "chat")]
        HttpRequestData request)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body);

            if (!document.RootElement.TryGetProperty("message", out var messageElement)
                || messageElement.ValueKind != JsonValueKind.String)
            {
                var badRequest = request.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new
                {
                    error = "Envía un JSON con la propiedad message de tipo texto."
                });
                return badRequest;
            }

            var message = messageElement.GetString()?.Trim();

            if (string.IsNullOrWhiteSpace(message))
            {
                var badRequest = request.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new
                {
                    error = "El mensaje no puede estar vacío."
                });
                return badRequest;
            }

            var response = await _responsesClient.CreateResponseAsync(message);
            var answer = response.Value.GetOutputText();

            var result = request.CreateResponse(HttpStatusCode.OK);
            await result.WriteAsJsonAsync(new { answer });
            return result;
        }
        catch (JsonException)
        {
            var badRequest = request.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new
            {
                error = "El cuerpo de la solicitud no contiene JSON válido."
            });
            return badRequest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar el agente de Azure AI Foundry.");

            var errorResponse = request.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new
            {
                error = "No fue posible procesar el mensaje con el agente."
            });
            return errorResponse;
        }
    }
}
