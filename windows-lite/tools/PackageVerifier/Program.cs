using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length != 5) throw new ArgumentException("managed-setup setup-exe full-zip portable-exe app-dll");
using var stream = File.OpenRead(args[0]); using var pe = new PEReader(stream);
if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null) throw new Exception("Managed installer metadata absent");
var metadata = pe.GetMetadataReader(); var data = pe.GetSectionData(pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress).GetContent();
var resources = new Dictionary<string, byte[]>();
foreach (var handle in metadata.ManifestResources)
{
    var resource = metadata.GetManifestResource(handle); string name = metadata.GetString(resource.Name);
    if (name is not ("payload.zip" or "payload.sha256")) continue;
    if (!resource.Implementation.IsNil) throw new Exception("External payload resource");
    var block = data.AsSpan()[checked((int)resource.Offset)..]; int size = BinaryPrimitives.ReadInt32LittleEndian(block);
    if (size < 1 || size > block.Length - 4) throw new Exception("Bad resource length"); resources.Add(name, block.Slice(4, size).ToArray());
}
string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))).ToLowerInvariant();
if (resources.Count != 2 || Convert.ToHexString(SHA256.HashData(resources["payload.zip"])).ToLowerInvariant() != expected || Encoding.ASCII.GetString(resources["payload.sha256"]).Trim() != expected) throw new Exception("Embedded payload/checksum differs");
if (File.ReadAllBytes(args[1]).AsSpan().IndexOf(File.ReadAllBytes(args[0])) < 0) throw new Exception("Setup bundle does not contain exact verified managed installer");
if (File.ReadAllBytes(args[3]).AsSpan().IndexOf(File.ReadAllBytes(args[4])) < 0) throw new Exception("Portable bundle does not contain exact compiled app DLL");
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, embeddedPayloadSha256 = expected, embeddedChecksumMatches = true, exactManagedInstallerInSetupBundle = true, exactAppAssemblyInPortableBundle = true, windowsExecuted = false, assemblyLoaded = false }));
