// Does nothing harmful: writes the arguments it received (one per line) to args.txt next to itself, so the tests can
// check that the approved fixed arguments arrive unchanged, then waits up to 60 seconds and exits. Reads no input, opens
// no window and starts no other process.
File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "args.txt"), args);
Thread.Sleep(TimeSpan.FromSeconds(60));
