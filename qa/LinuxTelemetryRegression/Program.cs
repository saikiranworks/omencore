using System.Reflection;
using OmenCore.Linux.Hardware;

var fixture = Path.Combine(Path.GetTempPath(), "omencore-telemetry-" + Guid.NewGuid());
Directory.CreateDirectory(fixture);
try
{
    var cpuPath = Path.Combine(fixture, "cpu_input");
    var gpuPath = Path.Combine(fixture, "gpu_input");
    File.WriteAllText(cpuPath, "48000");
    File.WriteAllText(gpuPath, "42000");
    var hwmon = new LinuxHwMonController();
    SetPaths(hwmon, "_cpuSensorPaths", cpuPath);
    SetPaths(hwmon, "_gpuSensorPaths", gpuPath);
    var ec = new LinuxEcController();
    var cpu = LinuxTelemetryResolver.GetCpuTemperature(ec, hwmon);
    var gpu = LinuxTelemetryResolver.GetGpuTemperature(ec, hwmon);
    Check(cpu?.Temperature == 48 && cpu.Path == cpuPath, "CPU must report 48°C from hwmon over EC's 99°C");
    Check(gpu?.Temperature == 42 && gpu.Path == gpuPath, "GPU must prefer hwmon");
    Check(ec.Reads == 0, "EC must not be polled when hwmon is available");
    SetPaths(hwmon, "_cpuSensorPaths", Path.Combine(fixture, "missing"), cpuPath);
    Check(LinuxTelemetryResolver.GetCpuTemperature(ec, hwmon)?.Temperature == 48, "Unreadable primary sensor must fall back to the next hwmon sensor");
    SetPaths(hwmon, "_cpuSensorPaths");
    cpu = LinuxTelemetryResolver.GetCpuTemperature(ec, hwmon);
    Check(cpu?.Temperature == 99 && cpu.Source == "ec", "EC remains a fallback when CPU hwmon is absent");
    ec.CpuTemperature = null;
    Check(LinuxTelemetryResolver.GetCpuTemperature(ec, hwmon) == null, "Missing telemetry must remain unavailable");
    Console.WriteLine("PASS: hwmon priority, EC fallback, sensor failure fallback, and unavailable telemetry");
}
finally
{
    Directory.Delete(fixture, true);
}

static void SetPaths(LinuxHwMonController hwmon, string fieldName, params string[] paths)
{
    var pathsList = (List<string>)typeof(LinuxHwMonController)
        .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hwmon)!;
    pathsList.Clear();
    pathsList.AddRange(paths);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

// Simulate a legacy EC register that contains plausible but incorrect telemetry.
namespace OmenCore.Linux.Hardware
{
    public sealed class LinuxEcController
    {
        public int? CpuTemperature { get; set; } = 99;
        public int Reads { get; private set; }
        public int? GetCpuTemperature() { Reads++; return CpuTemperature; }
        public int? GetGpuTemperature() { Reads++; return 95; }
    }
}
