using Newtonsoft.Json;

namespace ClanSystem.Services
{
    /// <summary>
    /// Wire envelope every Cloud Code script returns. Expected failures travel as data rather than
    /// exceptions so the client can present them without parsing error strings.
    /// </summary>
    /// <typeparam name="T">Payload type carried on success.</typeparam>
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    internal class SocialEnvelope<T>
    {
        [JsonProperty("ok")] public bool IsOk { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("data")] public T Data { get; set; }
    }
}
