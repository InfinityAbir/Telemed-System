using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Telemed.Models;

namespace Telemed.Controllers
{
    [Authorize]
    public class DoctorSchedulesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public DoctorSchedulesController(ApplicationDbContext context)
        {
            _context = context;
        }

        [Authorize(Roles = "Doctor")]
        public async Task<IActionResult> MySchedules()
        {
            var userId = _context.Users
                .Where(u => u.UserName == User.Identity.Name)
                .Select(u => u.Id)
                .FirstOrDefault();

            var doctor = await _context.Doctors.FirstOrDefaultAsync(d => d.UserId == userId);
            if (doctor == null) return NotFound("Doctor not found.");

            var schedules = await _context.DoctorSchedules
                .Where(s => s.DoctorId == doctor.DoctorId)
                .OrderByDescending(s => s.Date)
                .ToListAsync();

            return View(schedules);
        }

        [Authorize(Roles = "Doctor")]
        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Doctor")]
        public async Task<IActionResult> Create(DoctorScheduleMultiDayViewModel model)
        {
            if (!ModelState.IsValid)
            {
                TempData["Error"] = "Please fill all required fields correctly.";
                return View(model);
            }

            var userId = _context.Users
                .Where(u => u.UserName == User.Identity.Name)
                .Select(u => u.Id)
                .FirstOrDefault();

            var doctor = await _context.Doctors.FirstOrDefaultAsync(d => d.UserId == userId);
            if (doctor == null)
            {
                TempData["Error"] = "Doctor profile not found.";
                return View(model);
            }

            // ✅ Use TimeSpan directly (no parsing needed)
            var startTime = model.StartTime;
            var endTime = model.EndTime;

            if (endTime <= startTime)
            {
                TempData["Error"] = "End time must be after start time.";
                return View(model);
            }

            for (var date = model.StartDate.Date; date <= model.EndDate.Date; date = date.AddDays(1))
            {
                bool exists = await _context.DoctorSchedules
                    .AnyAsync(s => s.DoctorId == doctor.DoctorId && s.Date == date);

                if (exists) continue;

                var newSchedule = new DoctorSchedule
                {
                    DoctorId = doctor.DoctorId,
                    Date = date,
                    StartTime = startTime,
                    EndTime = endTime,
                    MaxPatientsPerDay = model.MaxPatientsPerDay,
                    IsApproved = false,
                    VideoCallLink = model.VideoCallLink
                };

                _context.DoctorSchedules.Add(newSchedule);
            }

            await _context.SaveChangesAsync();
            TempData["Message"] = "Schedules created successfully and pending admin approval.";
            return RedirectToAction("MySchedules");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Doctor")]
        public async Task<IActionResult> EditSchedule(int ScheduleId, TimeSpan StartTime, TimeSpan EndTime, int MaxPatientsPerDay)
        {
            var schedule = await _context.DoctorSchedules.FindAsync(ScheduleId);
            if (schedule == null) return NotFound();

            if (EndTime <= StartTime)
            {
                TempData["Error"] = "End time must be after start time.";
                return RedirectToAction("MySchedules");
            }

            schedule.StartTime = StartTime;
            schedule.EndTime = EndTime;
            schedule.MaxPatientsPerDay = MaxPatientsPerDay;
            schedule.IsApproved = false; // Re-approve after edit

            _context.Update(schedule);
            await _context.SaveChangesAsync();

            TempData["Message"] = "Schedule updated successfully and sent for re-approval.";
            return RedirectToAction("MySchedules");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Doctor")]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var schedule = await _context.DoctorSchedules.FindAsync(id);
            if (schedule == null) return NotFound();

            // Prevent deletion if any appointment is linked
            bool hasAppointments = await _context.Appointments
                .AnyAsync(a => a.ScheduleId == schedule.ScheduleId);

            if (hasAppointments)
            {
                TempData["Error"] = "Cannot delete schedule with existing appointments.";
                return RedirectToAction("MySchedules");
            }

            _context.DoctorSchedules.Remove(schedule);
            await _context.SaveChangesAsync();

            TempData["Message"] = "Schedule deleted successfully.";
            return RedirectToAction("MySchedules");
        }


        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Pending()
        {
            var pending = await _context.DoctorSchedules
                .Include(s => s.Doctor).ThenInclude(d => d.User)
                .Where(s => !s.IsApproved)
                .ToListAsync();

            return View(pending);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<IActionResult> Approve(int id)
        {
            var schedule = await _context.DoctorSchedules.FindAsync(id);
            if (schedule == null) return NotFound();

            schedule.IsApproved = true;
            _context.Update(schedule);
            await _context.SaveChangesAsync();

            TempData["Message"] = "Schedule approved successfully.";
            return RedirectToAction(nameof(Pending));
        }
    }
}
