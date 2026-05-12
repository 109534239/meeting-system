using System.ComponentModel.DataAnnotations;

namespace InterviewProject.Models
{
    public class Room
    {
        public int Id { get; set; }

        [Required]
        public string RoomName { get; set; }
        public DateTime CreatedTime { get; set; }
    }
}
