namespace Sysacta.Core.Tools.SystemMonitoring;

public sealed class SystemNoArgs;

/// <summary>
/// The four read-only monitoring tools (spec §7.5). They take no arguments, change nothing, and return missing values as
/// null with a typed reason. Results are returned only to authorized calls; the audit log records the tool name and
/// outcome only (no result data, device names or ids).
/// </summary>
public sealed class SystemGetStatusTool(ISystemMonitor monitor) : SysactaTool<SystemNoArgs, SystemStatusData>
{
    public override ToolDescriptor Descriptor { get; } = new("system_get_status", ToolCategories.System, "Get system status",
        "Samples current usage over a ~1 second window: CPU (% Processor Time of all processor groups; Task Manager's frequency-scaled figure can differ), physical memory (OS-usable total, available, used), uptime, and per-GPU utilization (busiest engine, per-engine values) and GPU memory in use. Each GPU is identified by gpu_id (adapter LUID, valid until restart). Missing values are null with a reason; 0 is a real measurement. No per-process data is returned.",
        DefaultPolicy.On, true, []);

    protected override Task<ToolOutcome<SystemStatusData>> ExecuteAsync(SystemNoArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var reading = monitor.SampleStatus(SystemStatus.SamplingInterval, cancellationToken);
        var data = SystemStatus.Interpret(reading, SystemStatus.SamplingInterval);
        if (data.Cpu.Reason is not null && data.Memory.Reason is not null && data.UptimeSeconds is null && data.Gpus.Count == 0 && data.GpusReason is not null)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The system status could not be read.", retryable: true);
        }

        return Task.FromResult(new ToolOutcome<SystemStatusData>(data, Verification.NotApplicable));
    }
}

public sealed class SystemGetHardwareTool(ISystemMonitor monitor) : SysactaTool<SystemNoArgs, SystemHardwareData>
{
    public override ToolDescriptor Descriptor { get; } = new("system_get_hardware", ToolCategories.System, "Get hardware configuration",
        "Reads the Windows edition, version, build and architecture; each processor's model and core counts plus the total package/physical core/logical processor topology; installed RAM (SMBIOS) and OS-usable RAM; each GPU's name, PCI vendor/device id and DXGI memory sizes (64-bit, bytes); and the PC manufacturer and model. No serial numbers, product keys, machine/user/domain names or device paths. Items that could not be read are listed in unavailable.",
        DefaultPolicy.On, true, []);

    protected override Task<ToolOutcome<SystemHardwareData>> ExecuteAsync(SystemNoArgs args, ToolExecution execution, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolOutcome<SystemHardwareData>(SystemInventory.Hardware(monitor.ReadHardware()), Verification.NotApplicable));
}

public sealed class SystemGetTemperaturesTool(ISystemMonitor monitor) : SysactaTool<SystemNoArgs, SystemTemperaturesData>
{
    public override ToolDescriptor Descriptor { get; } = new("system_get_temperatures", ToolCategories.System, "Get temperatures",
        "Reads temperatures (°C) available to a normal user without drivers: each GPU's driver-reported temperature (WDDM adapter performance data; not specified as core, hotspot or memory) and ACPI thermal zones (firmware-defined, not CPU temperatures). CPU package/core, GPU hotspot/memory junction and drive temperatures are not read and are listed in not_collected. Values are never estimated; a sensor without a reading has temperature_c null and a reason.",
        DefaultPolicy.On, true, []);

    protected override Task<ToolOutcome<SystemTemperaturesData>> ExecuteAsync(SystemNoArgs args, ToolExecution execution, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolOutcome<SystemTemperaturesData>(SystemInventory.Temperatures(monitor.ReadTemperatures()), Verification.NotApplicable));
}

public sealed class SystemGetStorageTool(ISystemMonitor monitor) : SysactaTool<SystemNoArgs, SystemStorageData>
{
    public override ToolDescriptor Descriptor { get; } = new("system_get_storage", ToolCategories.System, "Get storage capacity",
        "Reads the capacity of local fixed drives with a drive letter: logical volume size, free space (ignoring quotas), space available to the current user, used bytes and usage percent. Logical volumes, not physical disks; removable, network, optical and SUBST drives are left out (excluded_count) and are never accessed. No volume labels, serial numbers, GUID paths or folder contents.",
        DefaultPolicy.On, true, []);

    protected override Task<ToolOutcome<SystemStorageData>> ExecuteAsync(SystemNoArgs args, ToolExecution execution, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolOutcome<SystemStorageData>(SystemInventory.Storage(monitor.ReadStorage()), Verification.NotApplicable));
}
