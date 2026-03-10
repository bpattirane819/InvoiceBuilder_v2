using System;
using Microsoft.Xrm.Sdk;

public sealed class ConsoleTracingService : ITracingService
{
    public void Trace(string format, params object[] args)
        => Console.WriteLine(format, args);

    public void Trace(string message)
        => Console.WriteLine(message);
}
