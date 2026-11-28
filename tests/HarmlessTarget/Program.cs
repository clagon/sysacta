// Does nothing: waits up to 60 seconds and exits, so a test that fails to clean up leaves nothing behind for long.
// Reads no input, writes nothing, opens no window and starts no other process.
Thread.Sleep(TimeSpan.FromSeconds(60));
