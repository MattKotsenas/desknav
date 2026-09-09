namespace Desknav.ControlPlane;

public sealed record PrepareForShutdown;

public sealed record RuntimeFailure(Exception Cause);
