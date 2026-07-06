using DocumentFormat.OpenXml.Spreadsheet;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    [Table("Certificatecategories")]
    public class Certificatecategories
    {
        [Key]
        public int Id { get; set; }

        public string CertCode { get; set; } = "";        // 職類代號 (例如: 07600)
        public string CertName { get; set; } = "";        // 職類名稱 (例如: 中餐烹調)
        public string AvailableLevels { get; set; } = "";  // 該職類有的級別 (例如: "乙/丙", "單一級")
    }
}