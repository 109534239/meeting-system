using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    // 🐛 這輪修正的根本原因：原本逐字稿的暫存區（_transcriptBuffer）是 RoomController 裡的
    //    「靜態記憶體變數」，只存在單一個 ASP.NET 執行程序（process）的記憶體裡。
    //
    //    這在「所有人連到同一台伺服器」的情況下沒問題，但這個專案實際的測試方式是
    //    每個人各自在自己的電腦上跑一份最新的程式碼（各自的 localhost:5216），
    //    只有 Jitsi 視訊本身是連到共用的雲端（8x8.vc），資料庫也是共用的雲端資料庫——
    //    但「逐字稿暫存」這一步，因為是存在記憶體裡、不是存進共用資料庫，
    //    所以每個人的音檔送出後，其實是存進「自己那台電腦上的那個 ASP.NET 程序」的記憶體裡，
    //    彼此完全看不到對方存了什麼。主持人按「結束會議」時，
    //    只會看到「主持人自己那台電腦」記憶體裡的內容——剛好就是只有主持人自己說的話，
    //    這就是「逐字稿一直只有最高主管」真正的根本原因，不是轉錄本身的問題。
    //
    //    修正方式：把這個暫存區從「記憶體」改存到大家共用的資料庫裡，這樣不管是誰、
    //    在哪一台電腦上送出音檔，主持人結束會議時去資料庫撈，就能撈到所有人送的內容。
    public class TranscriptChunkRecord
    {
        public int Id { get; set; }

        // 對應 Room.JitsiRoomName，不用外鍵關聯到 Room.Id 也可以查，省一次 join
        public string RoomCode { get; set; } = "";

        public string Speaker { get; set; } = "";
        public string Text { get; set; } = "";

        // 顯示用的時間字串（例如「上午 10:05:30」），跟原本記憶體版本的格式保持一致
        public string TimeLabel { get; set; } = "";

        // 排序用的實際時間（伺服器收到這段內容的時間，UTC）
        public DateTime ReceivedAt { get; set; }
    }
}
