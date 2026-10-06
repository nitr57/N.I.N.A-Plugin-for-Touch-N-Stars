using System;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using Newtonsoft.Json.Linq;
using TouchNStars.PHD2;
using TouchNStars.Server.Models;

namespace TouchNStars.Server.Controllers;

public partial class PHD2Controller
{
    private async Task<ApiResponse> AIResponse(string method, bool readBody = false)
    {
        try
        {
            JObject parameters = null;
            if (readBody)
            {
                try { parameters = JObject.Parse(await HttpContext.GetRequestBodyAsStringAsync()); }
                catch (Exception) { throw new ArgumentException("Expected a JSON object"); }
                if (parameters == null) throw new ArgumentException("Expected a JSON object");
            }
            EnsurePHD2ServicesInitialized();
            var result = await phd2Service.AIRequestAsync(method, parameters);
            return new ApiResponse { Success = true, Response = PHD2AIResponseData.ToPlain(result), StatusCode = 200, Type = "PHD2AI" };
        }
        catch (Exception ex)
        {
            bool unsupported = ex is PHD2Exception &&
                (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase));
            int code = unsupported ? 501 : ex is ArgumentException ? 400 :
                ex is PHD2Exception || ex is InvalidOperationException ? 409 : 500;
            HttpContext.Response.StatusCode = code;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = code,
                Type = unsupported ? "PHD2AIUnsupported" : "Error" };
        }
    }

    [Route(HttpVerbs.Get, "/phd2/ai/status")]
    public Task<ApiResponse> GetAIStatus() => AIResponse("ai_get_status");
    [Route(HttpVerbs.Get, "/phd2/ai/validate")]
    public Task<ApiResponse> ValidateAIModel() => AIResponse("ai_validate_model");
    [Route(HttpVerbs.Get, "/phd2/ai/models")]
    public Task<ApiResponse> GetAIModels() => AIResponse("ai_list_models");
    [Route(HttpVerbs.Put, "/phd2/ai/mode")]
    public Task<ApiResponse> SetAIMode() => AIResponse("ai_set_mode", true);
    [Route(HttpVerbs.Get, "/phd2/ai/gain")]
    public Task<ApiResponse> GetAIGain() => AIResponse("ai_get_prediction_gain");
    [Route(HttpVerbs.Put, "/phd2/ai/gain")]
    public Task<ApiResponse> SetAIGain() => AIResponse("ai_set_prediction_gain", true);
    [Route(HttpVerbs.Post, "/phd2/ai/models/select")]
    public Task<ApiResponse> SelectAIModel() => AIResponse("ai_select_model", true);
    [Route(HttpVerbs.Post, "/phd2/ai/models/import")]
    public Task<ApiResponse> ImportAIModel() => AIResponse("ai_import_model", true);
    [Route(HttpVerbs.Post, "/phd2/ai/models/export")]
    public Task<ApiResponse> ExportAIModel() => AIResponse("ai_export_model", true);
    [Route(HttpVerbs.Post, "/phd2/ai/models/unload")]
    public Task<ApiResponse> UnloadAIModel() => AIResponse("ai_unload_model");
    [Route(HttpVerbs.Post, "/phd2/ai/training/start")]
    public Task<ApiResponse> StartAITraining() => AIResponse("ai_start_training", true);
    [Route(HttpVerbs.Post, "/phd2/ai/training/fit")]
    public Task<ApiResponse> FitAIRecording() => AIResponse("ai_train_model", true);
    [Route(HttpVerbs.Get, "/phd2/ai/training/status")]
    public Task<ApiResponse> GetAITrainingStatus() => AIResponse("ai_get_training_status");
    [Route(HttpVerbs.Post, "/phd2/ai/training/cancel")]
    public Task<ApiResponse> CancelAITraining() => AIResponse("ai_cancel_training");
    [Route(HttpVerbs.Get, "/phd2/ai/recording/status")]
    public Task<ApiResponse> GetAIRecordingStatus() => AIResponse("ai_get_characterization_status");
    [Route(HttpVerbs.Post, "/phd2/ai/recording/start")]
    public Task<ApiResponse> StartAIRecording() => AIResponse("ai_start_characterization", true);
    [Route(HttpVerbs.Post, "/phd2/ai/recording/stop")]
    public Task<ApiResponse> StopAIRecording() => AIResponse("ai_stop_characterization");
}
