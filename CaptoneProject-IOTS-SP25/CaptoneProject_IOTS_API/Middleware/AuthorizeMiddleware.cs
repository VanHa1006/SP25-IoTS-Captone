﻿namespace CaptoneProject_IOTS_API.Middleware
{
    public class AuthorizeMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<AuthorizeMiddleware> _logger;

        public AuthorizeMiddleware(RequestDelegate next, ILogger<AuthorizeMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred");

                context.Response.ContentType = "application/json";
                context.Response.StatusCode = 500;

                var response = new
                {
                    Code = 500,
                    Message = ex.Message,
                    Detailed = ex.StackTrace
                };

                var responseText = System.Text.Json.JsonSerializer.Serialize(response);

                await context.Response.WriteAsync(responseText);
            }

            if (context.Response.StatusCode == 401 || context.Response.StatusCode == 403)
            {
                var statusCode = context.Response.StatusCode;
                var message = statusCode == 401 ? "Unauthorized access" : "Forbidden access";

                _logger.LogError($"Status code: {statusCode}");

                context.Response.ContentType = "application/json";

                var response = new
                {
                    Code = statusCode,
                    Message = message,
                };
                
                var responseText = System.Text.Json.JsonSerializer.Serialize(response);

                await context.Response.WriteAsync(responseText);
            }
        }
    }
}
