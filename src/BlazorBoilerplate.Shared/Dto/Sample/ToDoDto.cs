﻿using System.ComponentModel.DataAnnotations;

namespace BlazorBoilerplate.Shared.Dto
{
    public class TodoDto
    {
        [Key]
        public long Id { get; set; }

        [Required]
        public string Title { get; set; }
        public bool IsCompleted { get; set; }
    }
}