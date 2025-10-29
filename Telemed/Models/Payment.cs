namespace Telemed.Models
{
    public enum PaymentStatus
    {
        Pending,
        Paid
    }

    public class Payment
    {
        public int PaymentId { get; set; }
        public int AppointmentId { get; set; }
        public Appointment Appointment { get; set; }

        public decimal Amount { get; set; }

        // 🔹 Use enum instead of string
        public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

        public DateTime? PaymentDate { get; set; }
    }
}
