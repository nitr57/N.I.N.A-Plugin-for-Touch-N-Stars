using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TouchNStars.PHD2
{
    public partial class PHD2Client
    {
        // Explicit allowlist: the HTTP AI API cannot issue arbitrary mount RPCs.
        private static readonly Dictionary<string, string[]> AIParameters = new()
        {
            ["ai_get_status"] = [],
            ["ai_validate_model"] = [],
            ["ai_list_models"] = [],
            ["ai_set_storage_directory"] = ["path"],
            ["ai_get_training_status"] = [],
            ["ai_cancel_training"] = [],
            ["ai_unload_model"] = [],
            ["ai_get_prediction_gain"] = [],
            ["ai_get_characterization_status"] = [],
            ["ai_stop_characterization"] = [],
            ["ai_set_mode"] = ["mode"],
            ["ai_set_prediction_gain"] = ["gain"],
            ["ai_load_model"] = ["path"],
            ["ai_select_model"] = ["path"],
            ["ai_import_model"] = ["path"],
            ["ai_export_model"] = ["path"],
            ["ai_start_training"] = ["duration_sec", "period_sec"],
            ["ai_train_model"] = ["recording_path", "period_sec"],
            ["ai_start_characterization"] = ["mode", "duration_sec", "output_path"],
        };

        public JToken CallAI(string method, JObject parameters = null)
        {
            if (!AIParameters.TryGetValue(method, out var allowed))
                throw new ArgumentException("Unsupported AI method");
            parameters ??= new JObject();
            foreach (var property in parameters.Properties())
                if (Array.IndexOf(allowed, property.Name) < 0)
                    throw new ArgumentException("Unexpected AI parameter: " + property.Name);
            foreach (var name in new[] { "path", "recording_path", "output_path", "mode" })
                if (parameters.TryGetValue(name, out var value) &&
                    (value.Type != JTokenType.String || string.IsNullOrWhiteSpace(value.Value<string>())))
                    throw new ArgumentException(name + " must be a nonempty string");
            if (Array.IndexOf(allowed, "path") >= 0 && parameters["path"] == null)
                throw new ArgumentException("path is required");
            if (method == "ai_train_model" && parameters["recording_path"] == null)
                throw new ArgumentException("recording_path is required");
            if (method == "ai_set_mode" &&
                (parameters["mode"] == null || Array.IndexOf(new[] { "disabled", "shadow", "active" }, (string)parameters["mode"]) < 0))
                throw new ArgumentException("mode must be disabled, shadow or active");
            if (method == "ai_start_characterization" && (string)parameters["mode"] != "passive")
                throw new ArgumentException("Characterization mode must be passive");
            ValidateAINumber(parameters, "gain", 0, 1, method == "ai_set_prediction_gain");
            ValidateAINumber(parameters, "duration_sec", method == "ai_start_characterization" ? 30 : 60, 14400,
                method == "ai_start_characterization");
            ValidateAINumber(parameters, "period_sec", 30, 3600, false, true);
            if (method == "ai_start_training" && parameters["duration_sec"] != null && parameters["period_sec"] != null &&
                parameters["period_sec"].Value<double>() > 0 &&
                parameters["duration_sec"].Value<double>() < 2 * parameters["period_sec"].Value<double>())
                throw new ArgumentException("Training duration must cover at least two periods");
            CheckConnected();
            return Call(method, parameters.HasValues ? parameters : null)["result"];
        }

        private static void ValidateAINumber(JObject parameters, string name, double min, double max,
            bool required, bool allowZero = false)
        {
            var value = parameters[name];
            if (value == null)
            {
                if (required) throw new ArgumentException(name + " is required");
                return;
            }
            if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                throw new ArgumentException(name + " must be numeric");
            double number = value.Value<double>();
            if (!double.IsFinite(number) || (!(allowZero && number == 0) && (number < min || number > max)))
                throw new ArgumentException(name + " is outside the supported range");
        }

        public JToken GetAIStatus() => CallAI("ai_get_status");
        public JToken GetAIModels() => CallAI("ai_list_models");
        public JToken GetAITrainingStatus() => CallAI("ai_get_training_status");
        public JToken CancelAITraining() => CallAI("ai_cancel_training");
    }
}
