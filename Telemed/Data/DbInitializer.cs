using Microsoft.AspNetCore.Identity;
using Telemed.Models;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var dbContext = serviceProvider.GetRequiredService<ApplicationDbContext>();

        // 1️⃣ Create roles if they do not exist
        string[] roles = new[] { "Admin", "Doctor", "Patient" };
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        // 2️⃣ Create default Admin
        var adminEmail = "admin@telemed.local";
        var admin = await userManager.FindByEmailAsync(adminEmail);
        if (admin == null)
        {
            admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                FullName = "System Admin",
                UserRole = "Admin",
                IsActive = true
            };

            var result = await userManager.CreateAsync(admin, "Admin@1234"); // change password after first run
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(admin, "Admin");
            }
        }

        // 3️⃣ Create default Doctor
        var doctorEmail = "doctor@telemed.local";
        var doctorUser = await userManager.FindByEmailAsync(doctorEmail);
        if (doctorUser == null)
        {
            doctorUser = new ApplicationUser
            {
                UserName = doctorEmail,
                Email = doctorEmail,
                FullName = "Dr. John Doe",
                UserRole = "Doctor",
                Gender = "Male",
                IsActive = true
            };

            var result = await userManager.CreateAsync(doctorUser, "Doctor@1234");
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(doctorUser, "Doctor");

                // Add doctor details to Doctors table
                if (!dbContext.Doctors.Any(d => d.UserId == doctorUser.Id))
                {
                    var doctor = new Doctor
                    {
                        UserId = doctorUser.Id,
                        Specialization = "General Medicine",
                        Qualification = "MBBS",
                        IsApproved = true
                    };
                    dbContext.Doctors.Add(doctor);
                    await dbContext.SaveChangesAsync();
                }
            }
        }

        // 4️⃣ Create default Patient
        var patientEmail = "patient@telemed.local";
        var patientUser = await userManager.FindByEmailAsync(patientEmail);
        if (patientUser == null)
        {
            patientUser = new ApplicationUser
            {
                UserName = patientEmail,
                Email = patientEmail,
                FullName = "Jane Smith",
                UserRole = "Patient",
                Gender = "Female",
                IsActive = true
            };

            var result = await userManager.CreateAsync(patientUser, "Patient@1234");
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(patientUser, "Patient");

                // Add patient details to Patients table
                if (!dbContext.Patients.Any(p => p.UserId == patientUser.Id))
                {
                    var patient = new Patient
                    {
                        UserId = patientUser.Id,
                        DOB = new DateTime(1990, 1, 1),
                        Gender = "Female",
                        ContactNumber = "2222222222"
                    };
                    dbContext.Patients.Add(patient);
                    await dbContext.SaveChangesAsync();
                }
            }
        }
    }
}
