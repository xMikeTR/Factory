using System.ClientModel.Primitives;
using Google.Apis.Auth.OAuth2;

namespace Factory.Harness;

/// Sets `Authorization: Bearer <ADC token>` on each request to Vertex's OpenAI-compatible endpoint (Gemini and the
/// Model Garden models). The credential caches and refreshes its token, so a long run never sends an expired one.
public sealed class VertexAuthPolicy : PipelinePolicy
{
    private readonly Lazy<ITokenAccess> _credential = new(() =>
        GoogleCredential.GetApplicationDefault().CreateScoped("https://www.googleapis.com/auth/cloud-platform"));

    /// Claude has its own client; every other model id goes through the OpenAI-compatible endpoint.
    public static bool IsClaude(string model) => model.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    public static Uri OpenAIEndpoint(string project, string region) =>
        new($"https://{(region == "global" ? "" : region + "-")}aiplatform.googleapis.com/v1/projects/{project}/locations/{region}/endpoints/openapi");

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        message.Request.Headers.Set("Authorization", "Bearer " + _credential.Value.GetAccessTokenForRequestAsync().GetAwaiter().GetResult());
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        message.Request.Headers.Set("Authorization", "Bearer " + await _credential.Value.GetAccessTokenForRequestAsync());
        await ProcessNextAsync(message, pipeline, currentIndex);
    }
}
