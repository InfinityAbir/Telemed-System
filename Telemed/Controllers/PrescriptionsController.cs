using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Telemed.Models;
using Telemed.ViewModels;

namespace Telemed.Controllers
{
    [Authorize]
    public class PrescriptionsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PrescriptionsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: Prescriptions (Visible to both doctor & patient)
        public async Task<IActionResult> Index()
        {
            var userEmail = User.Identity?.Name;

            IQueryable<Prescription> prescriptions = _context.Prescriptions
                .Include(p => p.Appointment)
                    .ThenInclude(a => a.Patient)
                        .ThenInclude(pu => pu.User)
                .Include(p => p.Appointment)
                    .ThenInclude(a => a.Doctor)
                        .ThenInclude(d => d.User);

            // If logged in user is a patient, show only their prescriptions
            if (User.IsInRole("Patient"))
            {
                prescriptions = prescriptions
                    .Where(p => p.Appointment.Patient.User.Email == userEmail);
            }
            // If doctor, show prescriptions they created
            else if (User.IsInRole("Doctor"))
            {
                prescriptions = prescriptions
                    .Where(p => p.Appointment.Doctor.User.Email == userEmail);
            }

            return View(await prescriptions.ToListAsync());
        }

        // GET: Prescriptions/Create (Only for Doctor)
        [Authorize(Roles = "Doctor")]
        public IActionResult Create()
        {
            ViewData["AppointmentId"] = new SelectList(
                _context.Appointments
                    .Include(a => a.Patient)
                        .ThenInclude(p => p.User)
                    .Include(a => a.Doctor)
                        .ThenInclude(d => d.User)
                    .Where(a => a.Status == AppointmentStatus.Approved),
                "AppointmentId", "AppointmentId"
            );

            return View();
        }

        // POST: Prescriptions/Create (Only for Doctor)
        [HttpPost]
        [Authorize(Roles = "Doctor")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(PrescriptionViewModel vm)
        {
            if (!ModelState.IsValid)
            {
                ViewData["AppointmentId"] = new SelectList(
                    _context.Appointments,
                    "AppointmentId", "AppointmentId",
                    vm.AppointmentId
                );
                return View(vm);
            }

            var prescription = new Prescription
            {
                AppointmentId = vm.AppointmentId,
                MedicineName = vm.MedicineName,
                Dosage = vm.Dosage,
                Duration = vm.Duration,
                Notes = vm.Notes,
                CreatedAt = DateTime.Now
            };

            _context.Prescriptions.Add(prescription);
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }

        // Generate PDF (for doctor and patient)
        public IActionResult Pdf(int id)
        {
            var p = _context.Prescriptions
                .Include(x => x.Appointment)
                    .ThenInclude(a => a.Patient)
                        .ThenInclude(pu => pu.User)
                .Include(x => x.Appointment)
                    .ThenInclude(a => a.Doctor)
                        .ThenInclude(d => d.User)
                .FirstOrDefault(x => x.PrescriptionId == id);

            if (p == null) return NotFound();

            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(20);
                    page.Content().Column(col =>
                    {
                        col.Item().Text($"Prescription #{p.PrescriptionId}").FontSize(18).SemiBold();
                        col.Item().Text($"Patient: {p.Appointment.Patient.User.FullName}");
                        col.Item().Text($"Doctor: {p.Appointment.Doctor.User.FullName} ({p.Appointment.Doctor.Specialization})");
                        col.Item().Text($"Medicine: {p.MedicineName}");
                        col.Item().Text($"Dosage: {p.Dosage}");
                        col.Item().Text($"Duration: {p.Duration}");
                        col.Item().Text($"Notes: {p.Notes}");
                        col.Item().Text($"Date: {p.CreatedAt:yyyy-MM-dd}");
                    });
                });
            });

            var pdfBytes = doc.GeneratePdf();
            return File(pdfBytes, "application/pdf", $"prescription_{id}.pdf");
        }

        // POST: Update prescription via AJAX (only for Doctor)
        [HttpPost]
        [Authorize(Roles = "Doctor")]
        public async Task<IActionResult> UpdatePrescription(int id, [FromForm] Prescription updatedPrescription, IFormFile? file)
        {
            var prescription = await _context.Prescriptions.FindAsync(id);
            if (prescription == null)
                return NotFound();

            prescription.MedicineName = updatedPrescription.MedicineName;
            prescription.Dosage = updatedPrescription.Dosage;
            prescription.Duration = updatedPrescription.Duration;
            prescription.Notes = updatedPrescription.Notes;

            if (file != null && file.Length > 0)
            {
                var uploadDir = Path.Combine("wwwroot", "uploads", "prescriptions");
                if (!Directory.Exists(uploadDir))
                    Directory.CreateDirectory(uploadDir);

                var fileName = $"{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}";
                var filePath = Path.Combine(uploadDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                prescription.FilePath = $"/uploads/prescriptions/{fileName}";
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        private bool PrescriptionExists(int id)
        {
            return _context.Prescriptions.Any(e => e.PrescriptionId == id);
        }
    }
}
