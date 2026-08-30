namespace Aptabase.Avalonia;

public class AptabaseException : Exception
{
    public AptabaseException() { }
    public AptabaseException(string? message) : base(message) { }
    public AptabaseException(string? message, Exception? innerException) : base(message, innerException) { }
}

public class AptabaseConfigurationException : AptabaseException
{
    public AptabaseConfigurationException() { }
    public AptabaseConfigurationException(string? message) : base(message) { }
    public AptabaseConfigurationException(string? message, Exception? innerException) : base(message, innerException) { }
}

public class AptabaseTransmissionException : AptabaseException
{
    public int? StatusCode { get; }

    public AptabaseTransmissionException() { }
    public AptabaseTransmissionException(string? message) : base(message) { }
    public AptabaseTransmissionException(string? message, Exception? innerException) : base(message, innerException) { }
    public AptabaseTransmissionException(string? message, int statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}

public class AptabaseSerializationException : AptabaseException
{
    public AptabaseSerializationException() { }
    public AptabaseSerializationException(string? message) : base(message) { }
    public AptabaseSerializationException(string? message, Exception? innerException) : base(message, innerException) { }
}