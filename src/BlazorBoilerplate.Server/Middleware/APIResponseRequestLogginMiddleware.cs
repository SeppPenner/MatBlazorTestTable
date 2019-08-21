﻿using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.RegularExpressions;
//using System.Text.Json; //Does not work for this middleware, at least as in preview
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using BlazorBoilerplate.Server.Middleware.Wrappers;
using BlazorBoilerplate.Server.Middleware.Extensions;
using BlazorBoilerplate.Server.Services;
using BlazorBoilerplate.Shared;
using BlazorBoilerplate.Server.Models;
using BlazorBoilerplate.Server.Data;

namespace BlazorBoilerplate.Server.Middleware
{
    //Logging  -> https://salslab.com/a/safely-logging-api-requests-and-responses-in-asp-net-core
    //Response -> https://www.c-sharpcorner.com/article/asp-net-core-and-web-api-a-custom-wrapper-for-managing-exceptions-and-consiste/
    public class APIResponseRequestLogginMiddleware
    {
        private readonly RequestDelegate _next;
        ILogger<APIResponseRequestLogginMiddleware> _logger;
        private IApiLogService _apiLogService;
        private readonly Func<object, Task> _clearCacheHeadersDelegate;
        private readonly bool _enableAPILogging;  

        public APIResponseRequestLogginMiddleware(RequestDelegate next, bool enableAPILogging)
        {
            _next = next;
            _enableAPILogging = enableAPILogging;
            _clearCacheHeadersDelegate = ClearCacheHeaders;
        }

        public async Task Invoke(HttpContext httpContext, IApiLogService apiLogService, ILogger<APIResponseRequestLogginMiddleware> logger)
        {
            _logger = logger;
            _apiLogService = apiLogService;

            try
            {
                var request = httpContext.Request;
                if (IsSwagger(httpContext) || !request.Path.StartsWithSegments(new PathString("/api")))
                {
                    await _next(httpContext);
                }
                else
                {
                    Stopwatch stopWatch = Stopwatch.StartNew();
                    var requestTime = DateTime.UtcNow;

                    //  Enable seeking
                    httpContext.Request.EnableBuffering();
                    //  Read the stream as text
                    var requestBodyContent = await new StreamReader(httpContext.Request.Body).ReadToEndAsync();
                    //  Set the position of the stream to 0 to enable re-reading
                    httpContext.Request.Body.Position = 0;

                    var originalBodyStream = httpContext.Response.Body;

                    using (var responseBody = new MemoryStream())
                    {
                        httpContext.Response.Body = responseBody;

                        try
                        {                            
                            var response = httpContext.Response;
                            response.Body = responseBody;
                            await _next.Invoke(httpContext);
                                                        
                            string responseBodyContent = null;                                                                              
                             
                            if (httpContext.Response.StatusCode == (int)HttpStatusCode.OK)
                            {
                                responseBodyContent = await FormatResponse(response);
                                await HandleSuccessRequestAsync(httpContext, responseBodyContent, httpContext.Response.StatusCode);
                            }
                            else
                            {
                                await HandleNotSuccessRequestAsync(httpContext, httpContext.Response.StatusCode);
                            }

                            #region Log Request / Response
                            if (_enableAPILogging)
                            {
                                stopWatch.Stop();
                                await responseBody.CopyToAsync(originalBodyStream);

                                Guid userId = Guid.Empty;
                                try
                                {
                                    userId = httpContext.User.Identity.IsAuthenticated
                                            ? new Guid(httpContext.User.Claims.Where(c => c.Type == ClaimTypes.NameIdentifier).First().Value)
                                            : Guid.Empty;
                                }
                                catch { }                                

                                await SafeLog(requestTime,
                                    stopWatch.ElapsedMilliseconds,
                                    response.StatusCode,
                                    request.Method,
                                    request.Path,
                                    request.QueryString.ToString(), 
                                    requestBodyContent,
                                    responseBodyContent,
                                    httpContext.Connection.RemoteIpAddress.ToString(),
                                    userId
                                    );
                            }
                            #endregion 
                        }
                        catch (System.Exception ex)
                        {
                            _logger.LogWarning("An Inner Middleware exception occurred: " + ex.Message);
                            await HandleExceptionAsync(httpContext, ex);
                        }
                        finally
                        {
                            responseBody.Seek(0, SeekOrigin.Begin);
                            await responseBody.CopyToAsync(originalBodyStream);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // We can't do anything if the response has already started, just abort.
                if (httpContext.Response.HasStarted)
                {
                    _logger.LogWarning("A Middleware exception occurred, but response has already started!");
                    throw;
                }

                await HandleExceptionAsync(httpContext, ex);
                throw;
            }
        }
        
        private async Task HandleExceptionAsync(HttpContext httpContext, System.Exception exception)
        {
            _logger.LogError("Api Exception:", exception);

            ApiError apiError = null;
            APIResponse apiResponse = null;
            int code = 0;

            if (exception is ApiException)
            {
                var ex = exception as ApiException;
                apiError = new ApiError(ex.Message)
                {
                    ValidationErrors = ex.Errors,
                    ReferenceErrorCode = ex.ReferenceErrorCode,
                    ReferenceDocumentLink = ex.ReferenceDocumentLink
                };
                code = ex.StatusCode;
                httpContext.Response.StatusCode = code;

            }
            else if (exception is UnauthorizedAccessException)
            {
                apiError = new ApiError("Unauthorized Access");
                code = (int)HttpStatusCode.Unauthorized;
                httpContext.Response.StatusCode = code;
            }
            else
            {
#if !DEBUG
                var msg = "An unhandled error occurred.";
                string stack = null;
#else
                var msg = exception.GetBaseException().Message;
                string stack = exception.StackTrace;
#endif

                apiError = new ApiError(msg)
                {
                    Details = stack
                };
                code = (int)HttpStatusCode.InternalServerError;
                httpContext.Response.StatusCode = code;
            }

            httpContext.Response.ContentType = "application/json";

            apiResponse = new APIResponse(code, ResponseMessageEnum.Exception.GetDescription(), null, apiError);

            await httpContext.Response.WriteAsync(JsonConvert.SerializeObject(apiResponse));
        }

        private static Task HandleNotSuccessRequestAsync(HttpContext httpContext, int code)
        {
            httpContext.Response.ContentType = "application/json";

            ApiError apiError;

            if (code == (int)HttpStatusCode.NotFound)
            {
                apiError = new ApiError("The specified URI does not exist. Please verify and try again.");
            }
            else if (code == (int)HttpStatusCode.NoContent)
            {
                apiError = new ApiError("The specified URI does not contain any content.");
            }
            else
            {
                apiError = new ApiError("Your request cannot be processed. Please contact a support.");
            }

            APIResponse apiResponse = new APIResponse(code, ResponseMessageEnum.Failure.GetDescription(), null, apiError);
            httpContext.Response.StatusCode = code;
            return httpContext.Response.WriteAsync(JsonConvert.SerializeObject(apiResponse));
        }

        private static Task HandleSuccessRequestAsync(HttpContext httpContext, object body, int code)
        {
            httpContext.Response.ContentType = "application/json";
            string jsonString, bodyText;
            APIResponse apiResponse = null;

            if (!body.ToString().IsValidJson())
            {
                return httpContext.Response.WriteAsync(JsonConvert.SerializeObject(apiResponse));
            }
            else
            {
                bodyText = body.ToString();
            }

            dynamic bodyContent = JsonConvert.DeserializeObject<dynamic>(bodyText);
            Type type = bodyContent?.GetType();

            // Check to see if body is already an APIResponse Class type
            if (type.Equals(typeof(Newtonsoft.Json.Linq.JObject)))
            {
                apiResponse = JsonConvert.DeserializeObject<APIResponse>(bodyText);
                if (apiResponse.StatusCode != code) 
                {
                    apiResponse.StatusCode = code;
                }

                if ( (apiResponse.Result != null) || (!string.IsNullOrEmpty(apiResponse.Message)) )
                {
                    jsonString = JsonConvert.SerializeObject(apiResponse);
                }
                else
                {
                    apiResponse = new APIResponse(code, ResponseMessageEnum.Success.GetDescription(), bodyContent, null);
                    jsonString = JsonConvert.SerializeObject(apiResponse);
                }
            }
            else
            {
                apiResponse = new APIResponse(code, ResponseMessageEnum.Success.GetDescription(), bodyContent, null);
                jsonString = JsonConvert.SerializeObject(apiResponse);
            }

            return httpContext.Response.WriteAsync(jsonString);
        }

        private async Task<string> FormatResponse(HttpResponse response)
        {
            response.Body.Seek(0, SeekOrigin.Begin);
            var plainBodyText = await new StreamReader(response.Body).ReadToEndAsync();
            response.Body.Seek(0, SeekOrigin.Begin);
            return plainBodyText;
        }

        // TODO Review Getting a info from VS over the Disposable of the StreamReader
        //private async Task<string> FormatResponse(HttpResponse response)
        //{
        //    using (StreamReader reader = new StreamReader(response.Body))
        //    {
        //        response.Body.Seek(0, SeekOrigin.Begin);
        //        var plainBodyText = await reader.ReadToEndAsync();
        //        response.Body.Seek(0, SeekOrigin.Begin);
        //        return plainBodyText;
        //    }       
        //}

        private bool IsSwagger(HttpContext context)
        {
            return context.Request.Path.StartsWithSegments("/swagger");
        }

        private async Task SafeLog(DateTime requestTime,
                            long responseMillis,
                            int statusCode,
                            string method,
                            string path,
                            string queryString,
                            string requestBody,
                            string responseBody,
                            string ipAddress,
                            Guid userId)
        {
            // Do not log these events login, logout, getuserinfo...
            if ((path.ToLower().StartsWith("/api/authorize/")) ||
                (path.ToLower().StartsWith("/api/UserProfile/")) )
            {
                return;
            }

            if (requestBody.Length > 256)
            {
                requestBody = $"(Truncated to 200 chars) {requestBody.Substring(0, 200)}";
            }

            // If the response body was an ApiResponse we should just save the Result object
            if (responseBody.Contains("\"result\":"))
            {
                try
                {
                    APIResponse apiResponse = JsonConvert.DeserializeObject<APIResponse>(responseBody);
                    responseBody = Regex.Replace(apiResponse.Result.ToString(), @"(""[^""\\]*(?:\\.[^""\\]*)*"")|\s+", "$1"); 
                }
                catch { }
            }

            if (responseBody.Length > 256)
            {
                responseBody = $"(Truncated to 200 chars) {responseBody.Substring(0, 200)}";
            }

            if (queryString.Length > 256)
            {
                queryString = $"(Truncated to 200 chars) {queryString.Substring(0, 200)}";
            }

            await _apiLogService.Log(new ApiLogItem
            {
                RequestTime = requestTime,
                ResponseMillis = responseMillis,
                StatusCode = statusCode,
                Method = method,
                Path = path,
                QueryString = queryString,
                RequestBody = requestBody,
                ResponseBody = responseBody,
                IPAddress = ipAddress,
                UserId = userId
            });
        }
               
        private Task ClearCacheHeaders(object state)
        {
            var response = (HttpResponse)state;

            response.Headers[HeaderNames.CacheControl] = "no-cache";
            response.Headers[HeaderNames.Pragma] = "no-cache";
            response.Headers[HeaderNames.Expires] = "-1";
            response.Headers.Remove(HeaderNames.ETag);

            return Task.CompletedTask;
        }
    }
}
