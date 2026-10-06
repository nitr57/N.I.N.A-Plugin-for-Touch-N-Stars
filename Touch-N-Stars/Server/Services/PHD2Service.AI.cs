using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TouchNStars.Server.Services
{
    public partial class PHD2Service
    {
        // Keep calls serialized with existing PHD2 operations and propagate errors.
        // A failed request must never appear as a successful model/mode change.
        public async Task<JToken> AIRequestAsync(string method, JObject parameters = null)
        {
            await WaitForConnectionIfNeeded();
            return await Task.Run(() =>
            {
                lock (lockObject)
                {
                    if (client == null || !client.IsConnected)
                        throw new InvalidOperationException("PHD2 is not connected");
                    try
                    {
                        var result = client.CallAI(method, parameters);
                        lastError = null;
                        return result;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        throw;
                    }
                }
            });
        }
    }
}
