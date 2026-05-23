<<<<<<< HEAD
=======
﻿using System;
using System.ComponentModel.DataAnnotations.Schema;

>>>>>>> 8866344074956b5162d25ed86764797f0aef079f
namespace InterviewProject.Models
{
    public class Job
    {
        public int Id { get; set; }
<<<<<<< HEAD
        public string Title { get; set; } = "";          // 職缺名稱
        public string Department { get; set; } = "";     // 部門
        public string Location { get; set; } = "";       // 工作地點
        public string JobType { get; set; } = "fulltime"; // fulltime / parttime / intern
        public string Description { get; set; } = "";    // 職缺描述
        public string Requirements { get; set; } = "";   // 應徵條件
        public int Salary { get; set; } = 0;             // 薪資（月薪）
        public bool IsActive { get; set; } = true;       // 是否上架
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // 建立者（哪個 HR 新增的）
        public int CreatedBy { get; set; }
        public Member? Creator { get; set; }
=======

        public string Title { get; set; }

        public string Department { get; set; }

        public string Location { get; set; }

        public string JobType { get; set; }

        public string Description { get; set; }

        public string Requirements { get; set; }

        public int Salary { get; set; }

        public bool IsActive { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        // 修改這裡：將 string 改成 int
        public int CreatedBy { get; set; }

        [NotMapped]
        public string[] TagList => !string.IsNullOrEmpty(Requirements)
            ? Requirements.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
>>>>>>> 8866344074956b5162d25ed86764797f0aef079f
    }
}