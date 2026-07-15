using VRCX;

var root = Path.Combine(AppContext.BaseDirectory, "world-photo-reader-fixture");
Directory.CreateDirectory(root);
VRCX.Program.AppDataDirectory = Path.Combine(root, "app-data");
Directory.CreateDirectory(VRCX.Program.AppDataDirectory);

var plainPath = Path.Combine(root, "plain.png");
var validPath = Path.Combine(root, "valid.PNG");
var invalidExtensionPath = Path.Combine(root, "photo.jpg");
var pngBytes = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
);
File.WriteAllBytes(plainPath, pngBytes);
File.WriteAllBytes(validPath, pngBytes);
File.WriteAllBytes(invalidExtensionPath, pngBytes);

const string worldId = "wrld_11111111-2222-3333-4444-555555555555";
const string metadata = """
    {
      "application": "VRCX",
      "version": 1,
      "author": { "id": "usr_test" },
      "world": {
        "id": "wrld_11111111-2222-3333-4444-555555555555",
        "instanceId": "wrld_11111111-2222-3333-4444-555555555555:1",
        "name": "Reader Fixture"
      },
      "players": [],
      "timestamp": "2026-07-14T10:00:00Z"
    }
    """;

using (var png = new PNGFile(validPath, true))
{
    Assert(png.WriteChunk(PNGHelper.GenerateTextChunk("Description", metadata)), "Could not add VRCX metadata to the PNG fixture.");
}

var reader = new ScreenshotWorldPhotoMetadataReader();
var valid = reader.Read(validPath);
Assert(valid.State == WorldPhotoParseState.Valid, $"Expected valid metadata, got {valid.State}/{valid.ErrorCode}.");
Assert(valid.WorldId == worldId, "The production reader did not preserve the exact embedded world ID.");
Assert(valid.CapturedAt == DateTimeOffset.Parse("2026-07-14T10:00:00Z"), "The production reader did not normalize the capture timestamp.");

var missing = reader.Read(plainPath);
Assert(missing.State == WorldPhotoParseState.NoMetadata, "A PNG without metadata must be excluded and cached as no-metadata.");

var wrongExtension = reader.Read(invalidExtensionPath);
Assert(wrongExtension.State == WorldPhotoParseState.Error && wrongExtension.ErrorCode == "not_png", "A non-PNG extension must be rejected before parsing.");

Console.WriteLine("PASS production PNG metadata reader");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
