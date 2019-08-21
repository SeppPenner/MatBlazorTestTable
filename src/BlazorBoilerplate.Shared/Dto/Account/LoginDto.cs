﻿using System.ComponentModel.DataAnnotations;

namespace BlazorBoilerplate.Shared.Dto
{
    public class LoginDto
    {
        [Required]
        public string UserName { get; set; }

        [Required]
        public string Password { get; set; }

        public bool RememberMe { get; set; }
    }
}
