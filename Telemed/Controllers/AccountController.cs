using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Telemed.Models;
using Telemed.ViewModels;

namespace Telemed.Controllers
{
    [Authorize]
    public class AccountController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _environment;
        private readonly IEmailSender _emailSender; // Email sender service

        public AccountController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RoleManager<IdentityRole> roleManager,
            ApplicationDbContext context,
            IWebHostEnvironment environment,
            IEmailSender emailSender)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _context = context;
            _environment = environment;
            _emailSender = emailSender;
        }

        // ---------------- LOGIN / REGISTER / LOGOUT ----------------
        [AllowAnonymous]
        [HttpGet]
        public IActionResult Login() => View();

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string email, string password, bool rememberMe = false)
        {
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                ModelState.AddModelError("", "Email and password are required.");
                return View();
            }

            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                ModelState.AddModelError("", "Invalid login attempt.");
                return View();
            }

            if (await _userManager.IsInRoleAsync(user, "Doctor"))
            {
                var doctor = await _context.Doctors.FirstOrDefaultAsync(d => d.UserId == user.Id);
                if (doctor != null && !doctor.IsApproved)
                {
                    ModelState.AddModelError("", "Your account is pending admin approval.");
                    return View();
                }
            }

            var result = await _signInManager.PasswordSignInAsync(user, password, rememberMe, false);
            if (result.Succeeded)
            {
                if (await _userManager.IsInRoleAsync(user, "Admin"))
                    return RedirectToAction("PendingDoctors", "Admin");

                return RedirectToAction("Profile");
            }

            ModelState.AddModelError("", "Invalid login attempt.");
            return View();
        }

        [AllowAnonymous]
        [HttpGet]
        public IActionResult Register() => View();

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            if ((model.Bio?.Length ?? 0) > 1000)
            {
                ModelState.AddModelError(nameof(model.Bio), "Bio / clinic address cannot exceed 1000 characters.");
                return View(model);
            }

            var existingUser = await _userManager.FindByEmailAsync(model.Email);
            if (existingUser != null)
            {
                ModelState.AddModelError("", "This email is already registered.");
                return View(model);
            }

            var user = new ApplicationUser
            {
                FullName = model.FullName,
                Email = model.Email,
                UserName = model.Email,
                PhoneNumber = model.ContactNumber,
                Address = model.Bio
            };

            var createResult = await _userManager.CreateAsync(user, model.Password);
            if (!createResult.Succeeded)
            {
                foreach (var error in createResult.Errors)
                    ModelState.AddModelError("", error.Description);
                return View(model);
            }

            if (!await _roleManager.RoleExistsAsync("Doctor"))
                await _roleManager.CreateAsync(new IdentityRole("Doctor"));
            if (!await _roleManager.RoleExistsAsync("Patient"))
                await _roleManager.CreateAsync(new IdentityRole("Patient"));

            if (model.RegisterAs == "Doctor")
            {
                await _userManager.AddToRoleAsync(user, "Doctor");
                var doctor = new Doctor
                {
                    UserId = user.Id,
                    Specialization = model.Specialization,
                    BMDCNumber = model.BMDCNumber,
                    Qualification = model.Qualification,
                    IsApproved = false,
                    ConsultationFee = model.ConsultationFee ?? 0 // fallback to 0 if null
                };
                _context.Doctors.Add(doctor);
            }
            else
            {
                await _userManager.AddToRoleAsync(user, "Patient");
                var patient = new Patient
                {
                    UserId = user.Id,
                    DOB = model.DOB,
                    Gender = model.Gender,
                    ContactNumber = model.ContactNumber
                };
                _context.Patients.Add(patient);
            }

            await _context.SaveChangesAsync();
            TempData["Message"] = "Registration successful! You can now log in.";
            return RedirectToAction("Login");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Login");
        }

        // ---------------- PROFILE (GET & POST) ----------------
        [HttpGet]
        public async Task<IActionResult> Profile()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            if (await _userManager.IsInRoleAsync(user, "Doctor"))
            {
                var doctor = await _context.Doctors.FirstOrDefaultAsync(d => d.UserId == user.Id);
                ViewBag.Specialization = doctor?.Specialization;
                ViewBag.Qualification = doctor?.Qualification;
                ViewBag.BMDCNumber = doctor?.BMDCNumber;
                ViewBag.IsApproved = doctor?.IsApproved;
                ViewBag.ConsultationFee = doctor?.ConsultationFee ?? 0;
            }
            else if (await _userManager.IsInRoleAsync(user, "Patient"))
            {
                var patient = await _context.Patients.FirstOrDefaultAsync(p => p.UserId == user.Id);
                ViewBag.DOB = patient?.DOB?.ToString("yyyy-MM-dd");
                ViewBag.Gender = patient?.Gender;
            }

            return View(user);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Profile(ApplicationUser model, decimal? ConsultationFee)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            user.FullName = model.FullName;
            user.Address = model.Address;
            user.PhoneNumber = model.PhoneNumber;

            if (!string.Equals(user.Email, model.Email, StringComparison.OrdinalIgnoreCase))
            {
                user.Email = model.Email;
                user.UserName = model.Email;
            }

            var result = await _userManager.UpdateAsync(user);

            if (await _userManager.IsInRoleAsync(user, "Doctor"))
            {
                var doctor = await _context.Doctors.FirstOrDefaultAsync(d => d.UserId == user.Id);
                if (doctor != null)
                {
                    if (ConsultationFee.HasValue)
                        doctor.ConsultationFee = ConsultationFee.Value;

                    if (Request.Form.ContainsKey("BMDCNumber"))
                        doctor.BMDCNumber = Request.Form["BMDCNumber"];

                    _context.Doctors.Update(doctor);
                    await _context.SaveChangesAsync();
                }
            }

            TempData["Message"] = result.Succeeded ? "Profile updated successfully!" : "Failed to update profile.";
            return RedirectToAction("Profile");
        }

        // ---------------- PROFILE IMAGE ----------------
        [HttpPost]
        public async Task<IActionResult> UploadProfileImage(IFormFile ProfileImage)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            if (ProfileImage != null && ProfileImage.Length > 0)
            {
                string uploadsFolder = Path.Combine(_environment.WebRootPath, "images");
                Directory.CreateDirectory(uploadsFolder);

                string fileName = $"{Guid.NewGuid()}_{ProfileImage.FileName}";
                string filePath = Path.Combine(uploadsFolder, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await ProfileImage.CopyToAsync(stream);
                }

                user.ProfileImageUrl = "/images/" + fileName;
                await _userManager.UpdateAsync(user);
                TempData["Message"] = "Profile picture updated successfully!";
            }

            return RedirectToAction("Profile");
        }

        // ---------------- CHANGE PASSWORD ----------------
        [HttpPost]
        public async Task<IActionResult> ChangePassword(string CurrentPassword, string NewPassword, string ConfirmPassword)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            if (NewPassword != ConfirmPassword)
            {
                TempData["Message"] = "New password and confirmation do not match.";
                return RedirectToAction("Profile");
            }

            var result = await _userManager.ChangePasswordAsync(user, CurrentPassword, NewPassword);
            TempData["Message"] = result.Succeeded ? "Password changed successfully!" : "Failed to change password.";
            return RedirectToAction("Profile");
        }

        // ---------------- FORGOT PASSWORD ----------------
        [AllowAnonymous]
        [HttpGet]
        public IActionResult ForgotPassword()
        {
            return View();
        }

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(string email)
        {
            if (string.IsNullOrEmpty(email))
            {
                TempData["Message"] = "Please enter your email.";
                return View();
            }

            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                TempData["Message"] = "If an account with this email exists, a reset link will be sent.";
                return View();
            }

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var resetLink = Url.Action("ResetPassword", "Account",
                new { email = user.Email, token = token }, Request.Scheme);

            // Modern HTML email
            var htmlMessage = $@"
    <!DOCTYPE html>
    <html lang='en'>
    <head>
        <meta charset='UTF-8'>
        <meta name='viewport' content='width=device-width, initial-scale=1.0'>
        <title>Reset Password - TeleMed</title>
        <style>
            body {{ font-family: Arial, sans-serif; background-color: #f4f4f7; margin: 0; padding: 0; }}
            .email-container {{ max-width: 600px; margin: 30px auto; background-color: #fff; padding: 30px; border-radius: 10px; box-shadow: 0 4px 15px rgba(0,0,0,0.1); }}
            .logo {{ font-size: 2rem; font-weight: bold; color: #007bff; text-align: center; margin-bottom: 20px; }}
            .content {{ font-size: 16px; color: #333; line-height: 1.5; }}
            .btn {{ display: inline-block; padding: 12px 20px; margin: 20px 0; background-color: #007bff; color: #fff !important; text-decoration: none; border-radius: 8px; font-weight: bold; }}
            .footer {{ font-size: 12px; color: #999; text-align: center; margin-top: 20px; }}
        </style>
    </head>
    <body>
        <div class='email-container'>
            <div class='logo'>TeleMed</div>
            <div class='content'>
                <p>Hello {user.FullName},</p>
                <p>You have requested to reset your password for your TeleMed account.</p>
                <p style='text-align:center;'>
                    <a href='{HtmlEncoder.Default.Encode(resetLink)}' class='btn'>Reset Password</a>
                </p>
                <p>If you did not request this, please ignore this email.</p>
                <p>Thanks,<br>TeleMed Team</p>
            </div>
            <div class='footer'>&copy; {DateTime.Now.Year} TeleMed. All rights reserved.</div>
        </div>
    </body>
    </html>";

            // Send email
            await _emailSender.SendEmailAsync(user.Email, "Reset Password - TeleMed", htmlMessage);

            TempData["Message"] = "If an account with this email exists, a reset link will be sent.";
            return View();
        }


        // ---------------- RESET PASSWORD ----------------
        
        [AllowAnonymous]
        [HttpGet]
        public IActionResult ResetPassword(string token, string email)
        {
            if (token == null || email == null) return RedirectToAction("Login");
            var model = new ResetPasswordViewModel { Token = token, Email = email };
            return View(model);
        }

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                TempData["Message"] = "Password reset successful.";
                return RedirectToAction("Login");
            }

            var result = await _userManager.ResetPasswordAsync(user, model.Token, model.Password);
            if (result.Succeeded)
            {
                TempData["Message"] = "Password reset successful.";
                return RedirectToAction("Login");
            }

            foreach (var error in result.Errors)
                ModelState.AddModelError("", error.Description);

            return View(model);
        }
    }
}
