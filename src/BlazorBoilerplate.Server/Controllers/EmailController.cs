﻿using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using BlazorBoilerplate.Server.Helpers;
using BlazorBoilerplate.Server.Services;
using BlazorBoilerplate.Shared;
using BlazorBoilerplate.Shared.Dto;
using BlazorBoilerplate.Server.Middleware.Wrappers;

namespace BlazorBoilerplate.Server.Controllers
{
    [Route("api/[controller]")]
    [Authorize]
    [ApiController]
    public class EmailController : ControllerBase
    {
        // Logger instance
        ILogger<EmailController> _logger;
        IEmailService _emailService;

        public EmailController(IEmailService emailService, ILogger<EmailController> logger)
        {
            _logger = logger;
            _emailService = emailService;
        }

        [HttpPost("Send")]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        public async Task<APIResponse> Send(EmailDto parameters)
        {
            if (!ModelState.IsValid)
            {
                return new APIResponse(400, "User Model is Invalid");
            }

            var email = new EmailMessageDto();

            email.ToAddresses.Add(new EmailAddressDto(parameters.ToName, parameters.ToAddress));

            //This forces all emails from the API to use the Test template to prevent spam
            parameters.TemplateName = "Test";

            //Send a Template email or a custom one since it is hardcoded to Test template it will not do a custom email.
            if (!string.IsNullOrEmpty(parameters.TemplateName))
            {
                switch (parameters.TemplateName)
                {
                    case "Test":
                        email = EmailTemplates.BuildTestEmail(email); //example of email Template usage
                        break;
                    default:
                        break;
                }
            }
            else
            {
                email.Subject = parameters.Subject;
                email.Body = parameters.Body;
            }

            //Add a new From Address if you so choose, default is set in appsettings.json
            //email.FromAddresses.Add(new EmailAddress("New From Name", "email@domain.com"));
            _logger.LogInformation("Test Email: {0}", email.Subject);

            await _emailService.SendEmailAsync(email);

            return new APIResponse(200, "Email Successfuly Sent");
        }

        [HttpGet("Receive")]
        [Authorize]
        public async Task<APIResponse> Receive()
        {
            //Check email from default account defined in appsettings.json
            //Currently set up to only send valid results, no error codes

            // To use Imap::
            var results = await _emailService.ReceiveMailImapAsync();

            // To use Pop3 uncomment the following and comment out the Imap line (above):
            //var results = await _emailService.ReceiveMailPopAsync();

            if (results.success)
            {
                return new APIResponse(200, "Email Received", results);
            }
            else
            {
                return new APIResponse(400, "Email Receiving Failed");
            }
        }
    }
}
