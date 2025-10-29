using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Telemed.Models;
using Telemed.ViewModels;

[Authorize]
public class AppointmentsController : Controller
{
    private readonly ApplicationDbContext _context;

    public AppointmentsController(ApplicationDbContext context)
    {
        _context = context;
    }

    // -------------------- PATIENT ACTIONS --------------------
    [Authorize(Roles = "Patient")]
    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var doctors = await _context.Doctors
            .Include(d => d.User)
            .Where(d => d.IsApproved)
            .Select(d => new { d.DoctorId, FullName = d.User.FullName, d.ConsultationFee })
            .ToListAsync();

        ViewBag.Doctors = doctors;
        var model = new AppointmentCreateViewModel
        {
            ScheduledAt = DateTime.Now.AddDays(1)
        };
        return View(model);
    }

    [Authorize(Roles = "Patient")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AppointmentCreateViewModel model)
    {
        if (!ModelState.IsValid)
        {
            var doctors = await _context.Doctors
                .Include(d => d.User)
                .Where(d => d.IsApproved)
                .Select(d => new { d.DoctorId, FullName = d.User.FullName, d.ConsultationFee })
                .ToListAsync();
            ViewBag.Doctors = doctors;
            return View(model);
        }

        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim))
            return Json(new { success = false, message = "Invalid session. Please log in again." });

        var patient = await _context.Patients.FirstOrDefaultAsync(p => p.UserId == userIdClaim);
        if (patient == null)
            return Json(new { success = false, message = "Patient not found." });

        var schedule = await _context.DoctorSchedules
            .FirstOrDefaultAsync(s =>
                s.DoctorId == model.DoctorId &&
                s.Date == model.ScheduledAt.Date &&
                s.IsApproved);

        if (schedule == null)
            return Json(new { success = false, message = "Doctor has no schedule on this date." });

        var totalMinutes = (schedule.EndTime - schedule.StartTime).TotalMinutes;
        var slotDuration = TimeSpan.FromMinutes(Math.Max(10, totalMinutes / schedule.MaxPatientsPerDay));

        var scheduleStart = schedule.Date.Add(schedule.StartTime);
        var scheduleEnd = schedule.Date.Add(schedule.EndTime);
        var requestedTime = model.ScheduledAt;

        if (requestedTime < scheduleStart || requestedTime >= scheduleEnd)
            return Json(new { success = false, message = "Selected time is outside the doctor's schedule." });

        var minutesFromStart = (requestedTime - scheduleStart).TotalMinutes;
        var slotIndex = (int)Math.Round(minutesFromStart / slotDuration.TotalMinutes);
        var nearestSlot = scheduleStart.AddMinutes(slotIndex * slotDuration.TotalMinutes);

        if (await _context.Appointments.AnyAsync(a =>
            a.DoctorId == model.DoctorId &&
            a.ScheduledAt == nearestSlot))
        {
            return Json(new { success = false, message = "This slot is already booked. Please choose another." });
        }

        var doctor = await _context.Doctors.FirstOrDefaultAsync(d => d.DoctorId == model.DoctorId);
        if (doctor == null)
            return Json(new { success = false, message = "Doctor not found." });

        decimal fee = doctor.ConsultationFee; // 🔹 Consultation fee

        var appointment = new Appointment
        {
            DoctorId = model.DoctorId,
            PatientId = patient.PatientId,
            ScheduledAt = nearestSlot,
            PatientNote = model.PatientNote ?? "",
            DoctorNote = "",
            Status = AppointmentStatus.PendingPayment,
            ScheduleId = schedule.ScheduleId
        };

        _context.Appointments.Add(appointment);
        await _context.SaveChangesAsync();

        // 🔹 Payment record for this appointment
        var payment = new Payment
        {
            AppointmentId = appointment.AppointmentId,
            Amount = fee,
            Status = PaymentStatus.Pending,
            PaymentDate = null
        };
        _context.Payments.Add(payment);
        await _context.SaveChangesAsync();

        // Redirect to payment page
        return RedirectToAction("Details", "Payments", new { id = payment.PaymentId });
    }

    [Authorize(Roles = "Patient")]
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim))
            return RedirectToAction("Login", "Account");

        var patient = await _context.Patients.FirstOrDefaultAsync(p => p.UserId == userIdClaim);
        if (patient == null)
            return RedirectToAction("Login", "Account");

        var appointments = await _context.Appointments
            .Where(a => a.PatientId == patient.PatientId)
            .Include(a => a.Doctor)
                .ThenInclude(d => d.User)
            .OrderByDescending(a => a.ScheduledAt)
            .ToListAsync();

        return View(appointments);
    }

    // -------------------- DOCTOR ACTIONS --------------------
    [Authorize(Roles = "Doctor")]
    [HttpGet]
    public async Task<IActionResult> DoctorIndex()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim))
            return RedirectToAction("Login", "Account");

        var doctor = await _context.Doctors
            .Include(d => d.Appointments)
                .ThenInclude(a => a.Patient)
                    .ThenInclude(p => p.User)
            .FirstOrDefaultAsync(d => d.UserId == userIdClaim);

        if (doctor == null)
            return RedirectToAction("Login", "Account");

        var appointments = doctor.Appointments
            .OrderBy(a => a.ScheduledAt)
            .ToList();

        return View("DoctorIndex", appointments);
    }

    [Authorize(Roles = "Doctor")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id)
    {
        var appointment = await _context.Appointments
            .Include(a => a.Doctor)
            .Include(a => a.Schedule)
            .FirstOrDefaultAsync(a => a.AppointmentId == id);

        if (appointment == null)
            return NotFound();

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (appointment.Doctor.UserId != userId)
            return Forbid();

        var schedule = await _context.DoctorSchedules
            .FirstOrDefaultAsync(s =>
                s.DoctorId == appointment.DoctorId &&
                s.Date == appointment.ScheduledAt.Date &&
                s.IsApproved);

        if (schedule != null)
            appointment.ScheduleId = schedule.ScheduleId;

        appointment.Status = AppointmentStatus.Approved;
        await _context.SaveChangesAsync();

        return RedirectToAction("DoctorIndex");
    }

    [Authorize(Roles = "Doctor")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id)
    {
        var appointment = await _context.Appointments
            .Include(a => a.Doctor)
            .FirstOrDefaultAsync(a => a.AppointmentId == id);

        if (appointment == null)
            return NotFound();

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (appointment.Doctor.UserId != userId)
            return Forbid();

        appointment.Status = AppointmentStatus.Rejected;
        await _context.SaveChangesAsync();

        return RedirectToAction("DoctorIndex");
    }

    // -------------------- COMMON ACTIONS --------------------
    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var appointment = await _context.Appointments
            .Include(a => a.Doctor)
                .ThenInclude(d => d.User)
            .Include(a => a.Patient)
                .ThenInclude(p => p.User)
            .Include(a => a.Schedule)
            .FirstOrDefaultAsync(a => a.AppointmentId == id);

        if (appointment == null)
            return NotFound();

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
            return Forbid();

        var isPatient = User.IsInRole("Patient") && appointment.Patient.UserId == userId;
        var isDoctor = User.IsInRole("Doctor") && appointment.Doctor.UserId == userId;
        var isAdmin = User.IsInRole("Admin");

        if (!isPatient && !isDoctor && !isAdmin)
            return Forbid();

        if (appointment.Schedule == null && appointment.ScheduleId.HasValue)
        {
            appointment.Schedule = await _context.DoctorSchedules
                .FirstOrDefaultAsync(s => s.ScheduleId == appointment.ScheduleId.Value);
        }

        if (appointment.ScheduleId == null)
        {
            var schedule = await _context.DoctorSchedules
                .FirstOrDefaultAsync(s =>
                    s.DoctorId == appointment.DoctorId &&
                    s.Date == appointment.ScheduledAt.Date &&
                    s.IsApproved);

            if (schedule != null)
            {
                appointment.ScheduleId = schedule.ScheduleId;
                appointment.Schedule = schedule;
                await _context.SaveChangesAsync();
            }
        }

        return View(appointment);
    }

    [Authorize(Roles = "Patient")]
    [HttpGet]
    public async Task<IActionResult> Book(int? doctorId)
    {
        var doctors = await _context.Doctors
            .Include(d => d.User)
            .Where(d => d.IsApproved)
            .Select(d => new { d.DoctorId, FullName = d.User.FullName, d.ConsultationFee })
            .ToListAsync();

        ViewBag.Doctors = doctors;

        var model = new AppointmentCreateViewModel
        {
            ScheduledAt = DateTime.Now.AddDays(1)
        };

        if (doctorId.HasValue)
        {
            // Preselect doctor and mark as fixed selection
            model.DoctorId = doctorId.Value;
            ViewBag.SelectedDoctorId = doctorId.Value;
            ViewBag.IsDoctorLocked = true;
        }
        else
        {
            ViewBag.SelectedDoctorId = null;
            ViewBag.IsDoctorLocked = false;
        }

        return View("Create", model);
    }




    [HttpGet]
    public async Task<IActionResult> GetAvailableSlots(int doctorId, DateTime date)
    {
        var schedule = await _context.DoctorSchedules
            .FirstOrDefaultAsync(s => s.DoctorId == doctorId && s.Date == date.Date && s.IsApproved);

        if (schedule == null)
            return Json(new { success = false, message = "Doctor has no approved schedule on this date." });

        if (schedule.EndTime <= schedule.StartTime)
            return Json(new { success = false, message = "Invalid schedule: End time must be after start time." });

        var totalMinutes = (schedule.EndTime - schedule.StartTime).TotalMinutes;
        if (totalMinutes < 1)
            return Json(new { success = false, message = "Doctor's available time is too short to create valid slots." });

        var slotDuration = TimeSpan.FromMinutes(Math.Max(1, totalMinutes / schedule.MaxPatientsPerDay));
        var slots = new List<DateTime>();
        var currentTime = schedule.Date.Add(schedule.StartTime);
        var endTime = schedule.Date.Add(schedule.EndTime);

        while (currentTime + slotDuration <= endTime)
        {
            slots.Add(currentTime);
            currentTime = currentTime.Add(slotDuration);
        }

        var bookedSlots = await _context.Appointments
            .Where(a => a.DoctorId == doctorId && a.ScheduledAt.Date == date.Date)
            .Select(a => a.ScheduledAt)
            .ToListAsync();

        var availableSlots = slots
            .Where(s => !bookedSlots.Any(b => b == s))
            .Select(s => s.ToString("HH:mm"))
            .ToList();

        if (!availableSlots.Any())
            return Json(new { success = false, message = "No available slots for this date." });

        // 🔹 Also return consultation fee for this doctor
        var doctor = await _context.Doctors.FindAsync(doctorId);
        decimal fee = doctor?.ConsultationFee ?? 0;

        return Json(new { success = true, slots = availableSlots, fee });
    }
}
