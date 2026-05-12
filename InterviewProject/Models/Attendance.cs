namespace 面試.Models
{
    public class Attendance
    {
        public int Id { get; set; }

        public string UserName { get; set; }

        public int RoomId { get; set; }

        public DateTime JoinTime { get; set; }

        public DateTime? LeaveTime { get; set; }
    }
}