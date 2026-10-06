# PHD2 native AI guiding

Touch-N-Stars calls this plugin's HTTP API. `PHD2Client.AI.cs` forwards the
allowlisted methods through the existing PHD2 TCP client and matches response
IDs while processing guide events. PINS uses the same API; pinsdaemon requires
no extension. Training/inference run inside PHD2, without Python or cloud APIs.

All routes below have prefix `/api/phd2/ai`. JSON responses use the existing
`{Success, Response, Error, StatusCode, Type}` envelope. `Response` retains native
PHD2's object/array/scalar types. HTTP 400 means invalid input, 409 means a PHD2
state/compatibility failure, 501 means an older PHD2 without AI RPCs, and 500
means an unexpected backend failure. The frontend displays native explanations.

| HTTP | Route | JSON body | Native RPC |
|---|---|---|---|
| GET | `/status` | — | `ai_get_status` |
| GET | `/validate` | — | `ai_validate_model` |
| GET | `/models` | — | `ai_list_models` |
| PUT | `/mode` | `{"mode":"disabled"}` (`shadow`, `active`) | `ai_set_mode` |
| GET | `/gain` | — | `ai_get_prediction_gain` |
| PUT | `/gain` | `{"gain":0.1}` | `ai_set_prediction_gain` |
| POST | `/models/select` | `{"path":"/home/pi/model.json"}` | `ai_select_model` |
| POST | `/models/import` | `{"path":"/home/pi/model.json"}` | `ai_import_model` |
| POST | `/models/export` | `{"path":"/home/pi/export.json"}` | `ai_export_model` |
| POST | `/models/unload` | — | `ai_unload_model` |
| POST | `/training/start` | `{"duration_sec":1800,"period_sec":300}` | `ai_start_training` |
| POST | `/training/fit` | `{"recording_path":"/home/pi/log.csv","period_sec":300}` | `ai_train_model` |
| GET | `/training/status` | — | `ai_get_training_status` |
| POST | `/training/cancel` | — | `ai_cancel_training` |
| GET | `/recording/status` | — | `ai_get_characterization_status` |
| POST | `/recording/start` | `{"mode":"passive","duration_sec":1800,"output_path":"/home/pi/new.csv"}` | `ai_start_characterization` |
| POST | `/recording/stop` | — | `ai_stop_characterization` |

Paths refer to the PHD2 host, not the HTTP/browser client. Import copies a model
into the current profile library; selection is a separate operation. Selection
and completed training leave AI disabled. `period_sec: 0` estimates the period.
Training is asynchronous: poll status until complete, failed or cancelled.
Known-period recording must span at least two cycles; six cycles are preferable.
Keep camera, optics, binning and profile unchanged while training. Native PHD2
checks calibration, hardware fingerprint and correction limits. AI changes RA;
DEC remains under its ordinary algorithm.

## Build against the actual PINS image

The image supplies its matching NINA assemblies. `PinsReferenceDirectory` is
optional and Linux-only; normal Windows and source-based Linux builds keep their
existing project references. `SkipPluginDeploy` suppresses the existing automatic
post-build copy so a built DLL can be tested before installing it.

```bash
export PATH=/home/pi/ai-guiding-test/dotnet:/home/pi/ai-guiding-test/node-v24.13.1-linux-arm64/bin:$PATH
bash scripts/build-pins.sh /home/pi/pins
dotnet test tests/PHD2AI/PHD2AI.Tests.csproj -c Release
```

The plugin is `Touch-N-Stars/bin/Release/net10.0/TouchNStars.dll`. The dedicated
test project links the actual TCP client and response serializer; it checks RPC
parameters, interleaved events, native failures and JSON types on Linux.

## Actual Pi integration test

Start PHD2's isolated built-in Simulator instance 96 with the PHD2 repository's
`tools/ai_guiding/launch_pi_simulator.sh`, then run:

```bash
node scripts/test-phd2-ai-pi.mjs http://127.0.0.1:5000 96
```

This performs real HTTP -> plugin -> PHD2 requests on the Pi: start/cancel,
training, import/export/select/unload, Shadow, bounded Active and offline fitting.
It refuses the main instance and non-simulator equipment, then disconnects the
simulator and restores the plugin's main-instance socket. Other clients must not
change that socket while the test runs. The ignored `scripts/ai-pi-test-result.json`
records results and model/CSV paths. It verifies software with simulated motion,
not an improvement on a physical mount. Full deployment and rollback instructions
are in PHD2's `doc/AI_GUIDING_PI.md`.

With the SkySimulator profile connected and capture stopped, run
`node scripts/test-skysimulator-camera.mjs http://127.0.0.1:5000` to check camera
exposure and JPEG delivery without calibrating or guiding. It validates both
device names first, starts looping briefly, then stops capture. Its ignored
`scripts/skysimulator-camera-test.jpg` preserves the returned image.
