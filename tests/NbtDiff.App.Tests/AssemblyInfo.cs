using Avalonia.Headless;

// Boots a single headless Avalonia session (with the real App as entry point) for this test assembly.
[assembly: AvaloniaTestApplication(typeof(NbtDiff.App.App))]
