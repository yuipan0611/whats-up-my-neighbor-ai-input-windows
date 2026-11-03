using System.Security.Cryptography;
using System.Text.Json;

// Isolated content/base-directory probe. No WPF, profile, identity, network or installer code.
try
{
    var files = new[] { "Assets/original.png", "Coats/original.json" }.Select(name => {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name));
        return new { path = name, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    }).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new { passed = true, baseDirectory = AppContext.BaseDirectory, processPath = Environment.ProcessPath, files }));
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException)
{
    Console.WriteLine(JsonSerializer.Serialize(new { passed = false, error = error.GetType().Name, baseDirectory = AppContext.BaseDirectory }));
    return 86;
}
