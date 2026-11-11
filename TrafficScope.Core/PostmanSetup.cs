namespace TrafficScope.Core;

/// <summary>Where the app gets its Postman client and key from. Tests swap these out.</summary>
public static class PostmanSetup
{
    public const string KeyName = "postman-api-key";
    public static Func<string, PostmanClient> ClientFactory { get; set; } = key => new PostmanClient(key);
    public static Func<string?> GetKey { get; set; } = () => SecretStore.Get(KeyName);
    public static Func<string, bool> SetKey { get; set; } = key => SecretStore.Set(KeyName, key);
    public static Action DeleteKey { get; set; } = () => SecretStore.Delete(KeyName);
}
