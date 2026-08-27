const startInfo = new ProcessStartInfo("https://github.com/determ1ne/CmdPalScripter/tree/master/ExampleScripts");
startInfo.UseShellExecute = true;
const process = Process.Start(startInfo);
if (process !== null) {
	process.Dispose();
}
