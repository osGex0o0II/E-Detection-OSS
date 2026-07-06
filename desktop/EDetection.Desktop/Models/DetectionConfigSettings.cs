namespace EDetection.Desktop.Models;

public sealed class DetectionConfigSettings
{
    public double VoltageMinThreshold { get; set; } = 353.0;

    public double VoltageMaxThreshold { get; set; } = 430.0;

    public double CurrentMaxThreshold { get; set; } = 1000.0;

    public double CurrentUnbalanceMaxThreshold { get; set; } = 0.15;

    public double ActivePowerMinThreshold { get; set; }

    public double PowerFactorMinThreshold { get; set; } = 0.9;

    public double TemperatureMinThreshold { get; set; }

    public double TemperatureMaxThreshold { get; set; } = 70.0;

    public double CurrentActiveMinThreshold { get; set; } = 1.0;

    public double FreezeCountThreshold { get; set; } = 3;

    public double FreezeStdThreshold { get; set; } = 0.01;

    public double VoltageImbalanceThreshold { get; set; } = 0.02;

    public bool CurrentOverloadEnabled { get; set; } = true;

    public bool CurrentUnbalanceEnabled { get; set; }

    public bool PowerFactorEnabled { get; set; }

    public bool DetailOutputEnabled { get; set; }
}
