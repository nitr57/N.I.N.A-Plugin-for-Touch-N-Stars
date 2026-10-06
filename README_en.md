# N.I.N.A Plugin for Touch-N-Stars

This plugin integrates [Touch'N'Stars](https://github.com/Touch-N-Stars/Touch-N-Stars) with the astrophotography software **NINA** (Nighttime Imaging 'N' Astronomy).

### 🚀 **Current Status: Beta Version**  
The plugin is currently in development and may contain bugs.

### 🔧 **Installation**
To use the plugin, copy the contents of the ZIP file to NINA's plugin directory.
This is usually located at: %LOCALAPPDATA%/NINA/Plugins/3.0.0/Touch-N-Stars
If the folder doesn't exist, please create it first.

The release package includes the offline `celestia-atlas-data` tree. Landscapes
created by Touch'N'Stars are stored outside the replaceable plugin directory,
normally below `~/.local/share/NINA/Touch-N-Stars/celestia-atlas-data/landscapes`
on Linux. Existing generated landscapes are migrated automatically.

### 🧩 **Important Notes** 
- The **Advanced API** plugin is required in the latest version.
  The API port must be set to 1888 and V2 must be enabled.
  Additionally, "Use Access-Control-Allow-Origin Header" must be enabled.
- Version 2.2.2.0 or newer is required for Three Point Polar Alignment.

### PHD2 AI guiding and PINS builds

The [existing PHD2 API reference](PHD2_API_README.md#native-ai-guiding) includes
native AI status, model management, training and mode controls. Training and
prediction run inside PHD2; PINS uses the same plugin API as Windows NINA.

For an image-based Linux build, install the .NET SDK matching the image and run
`bash scripts/build-pins.sh /home/pi/pins` (set `DOTNET` to the SDK executable if
it is not at the script's default location). This builds against the installed
NINA assemblies without automatically deploying the plugin. Run
`dotnet test tests/PHD2AI/PHD2AI.Tests.csproj -c Release` for TCP/API tests.
The DLL is `Touch-N-Stars/bin/Release/net10.0/TouchNStars.dll`.

For a live software check, launch PHD2's isolated built-in Simulator with
`tools/ai_guiding/launch_pi_simulator.sh` from the PHD2 repository, then run
`node scripts/test-phd2-ai-pi.mjs http://127.0.0.1:5000 96` from this repository.
The test refuses the main instance and non-simulator equipment, verifies
training/Shadow/Active, then restores the plugin's main-instance connection.
Other clients must not change that connection during the test. Its ignored
`scripts/ai-pi-test-result.json` contains results and model/recording paths.
With SkySimulator camera/mount connected and capture stopped,
`node scripts/test-skysimulator-camera.mjs http://127.0.0.1:5000` checks exposure
and JPEG delivery without calibration or guiding. Neither test establishes
improved guiding on a real mount. See PHD2's existing README for build/deployment
and rollback commands.
