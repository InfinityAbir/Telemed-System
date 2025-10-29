using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Telemed.Models;

namespace Telemed.Controllers
{
    [Authorize]
    public class PaymentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PaymentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // -------------------- LIST PAYMENTS --------------------
        // Admin sees all payments
        // Doctor sees payments for their appointments
        // Patient sees their own payments
        public async Task<IActionResult> Index()
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Forbid();

            IQueryable<Payment> paymentsQuery = _context.Payments
                .Include(p => p.Appointment)
                    .ThenInclude(a => a.Doctor)
                        .ThenInclude(d => d.User)
                .Include(p => p.Appointment)
                    .ThenInclude(a => a.Patient)
                        .ThenInclude(p => p.User)
                .OrderByDescending(p => p.PaymentDate);

            if (User.IsInRole("Admin"))
            {
                // Admin sees all payments
            }
            else if (User.IsInRole("Doctor"))
            {
                paymentsQuery = paymentsQuery.Where(p => p.Appointment.Doctor.UserId == userId);
            }
            else if (User.IsInRole("Patient"))
            {
                paymentsQuery = paymentsQuery.Where(p => p.Appointment.Patient.UserId == userId);
            }
            else
            {
                return Forbid();
            }

            var payments = await paymentsQuery.ToListAsync();
            return View(payments);
        }

        // -------------------- CREATE PAYMENT FOR APPOINTMENT --------------------
        // Automatically called after a patient books an appointment
        [Authorize(Roles = "Patient")]
        public async Task<Payment> CreateForAppointment(int appointmentId)
        {
            var appointment = await _context.Appointments
                .Include(a => a.Doctor)
                .FirstOrDefaultAsync(a => a.AppointmentId == appointmentId);

            if (appointment == null)
                throw new Exception("Appointment not found.");

            // Prevent duplicate payments
            var existing = await _context.Payments
                .FirstOrDefaultAsync(p => p.AppointmentId == appointmentId);
            if (existing != null)
                return existing;

            var payment = new Payment
            {
                AppointmentId = appointment.AppointmentId,
                Amount = appointment.Doctor.ConsultationFee,
                Status = PaymentStatus.Pending,
                PaymentDate = null
            };

            _context.Payments.Add(payment);
            await _context.SaveChangesAsync();

            return payment;
        }

        // -------------------- START PAYMENT --------------------
        // Shows payment details and "Pay Now" button
        [Authorize(Roles = "Patient")]
        public async Task<IActionResult> StartPayment(int appointmentId)
        {
            var payment = await _context.Payments
                .Include(p => p.Appointment)
                    .ThenInclude(a => a.Doctor)
                        .ThenInclude(d => d.User)
                .Include(p => p.Appointment)
                    .ThenInclude(a => a.Patient)
                        .ThenInclude(p => p.User)
                .FirstOrDefaultAsync(p => p.AppointmentId == appointmentId);

            if (payment == null)
            {
                payment = await CreateForAppointment(appointmentId);
            }

            return View(payment);
        }

        // -------------------- PROCESS ONLINE PAYMENT --------------------
        [Authorize(Roles = "Patient")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessPayment(int paymentId)
        {
            var payment = await _context.Payments
                .Include(p => p.Appointment)
                .FirstOrDefaultAsync(p => p.PaymentId == paymentId);

            if (payment == null) return NotFound();

            // Mark payment as paid
            payment.Status = PaymentStatus.Paid;
            payment.PaymentDate = DateTime.Now;

            // Update appointment status
            payment.Appointment.Status = AppointmentStatus.Completed;

            _context.Payments.Update(payment);
            await _context.SaveChangesAsync();

            // Redirect to StartPayment page to show confirmation
            return RedirectToAction("StartPayment", new { appointmentId = payment.AppointmentId });
        }

        // -------------------- BOOK APPOINTMENT --------------------
        // This action replaces any "Details" redirection after booking
        [Authorize(Roles = "Patient")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BookAppointment(int doctorId, DateTime scheduledAt)
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Forbid();

            // Create appointment
            var appointment = new Appointment
            {
                DoctorId = doctorId,
                PatientId = _context.Patients.First(p => p.UserId == userId).PatientId,
                ScheduledAt = scheduledAt,
                Status = AppointmentStatus.PendingPayment
            };

            _context.Appointments.Add(appointment);
            await _context.SaveChangesAsync();

            // Create payment and redirect to StartPayment
            await CreateForAppointment(appointment.AppointmentId);
            return RedirectToAction("StartPayment", new { appointmentId = appointment.AppointmentId });
        }

        // -------------------- HELPER --------------------
        private bool PaymentExists(int id)
        {
            return _context.Payments.Any(e => e.PaymentId == id);
        }
    }
}
