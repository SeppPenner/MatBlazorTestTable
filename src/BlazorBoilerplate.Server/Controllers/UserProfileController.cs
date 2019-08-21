﻿using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using BlazorBoilerplate.Server.Services;
using BlazorBoilerplate.Shared.Dto;
using BlazorBoilerplate.Server.Middleware.Wrappers;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace BlazorBoilerplate.Server.Controllers
{
    [Route("api/[controller]")]
    [Authorize]
    [ApiController]
    public class UserProfileController : ControllerBase
    {
        private readonly ILogger<UserProfileController> _logger;
        private readonly IUserProfileService _userProfileService;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public UserProfileController(IUserProfileService userProfileService, ILogger<UserProfileController> logger, IHttpContextAccessor httpContextAccessor)
        {
            _logger = logger;
            _userProfileService = userProfileService;
            _httpContextAccessor = httpContextAccessor;
        }

        // GET: api/UserProfile/Get
        [HttpGet("Get")]
        public async Task<APIResponse> Get()
        {
            Guid userId = new Guid(_httpContextAccessor.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value);
            return await _userProfileService.Get(userId);
        }

        [HttpPost("Upsert")]
        public async Task<APIResponse> Upsert(UserProfileDto userProfile)
        {
            if (!ModelState.IsValid)
            {
                return new APIResponse(400, "User Model is Invalid");
            }

            await _userProfileService.Upsert(userProfile);
            return new APIResponse(200, "Email Successfuly Sent");
        }

    }
}
