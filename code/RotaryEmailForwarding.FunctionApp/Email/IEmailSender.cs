using RotaryEmailForwarding.FunctionApp.Domain;

namespace RotaryEmailForwarding.FunctionApp.Email;

public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(OutboundEmailMessage message, CancellationToken cancellationToken);
}
