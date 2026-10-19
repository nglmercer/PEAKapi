using System;

namespace PeakRestApi.Core.Models;

/// <summary>
/// A failure that maps onto a specific HTTP status and machine readable error code.
/// </summary>
public sealed class ApiException : Exception
{
    public int Status { get; }

    public string Code { get; }

    public object? Details { get; }

    public ApiException(int status, string code, string message, object? details = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Details = details;
    }
}
