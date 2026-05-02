using WebApplication2.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace WebApplication2.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AppointmentsController : ControllerBase
    {
        private readonly string _connectionString;

        public AppointmentsController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") 
                ?? throw new InvalidOperationException("Connection string is missing.");
        }

        
        [HttpGet]
        public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
        {
            var appointments = new List<AppointmentListDto>();

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason,
                       p.FirstName + ' ' + p.LastName AS PatientFullName, p.Email AS PatientEmail
                FROM dbo.Appointments a
                JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
                WHERE (@Status IS NULL OR a.Status = @Status)
                  AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
                ORDER BY a.AppointmentDate;";

            await using var command = new SqlCommand(query, connection);

            command.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            command.Parameters.AddWithValue("@PatientLastName", (object?)patientLastName ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                appointments.Add(new AppointmentListDto
                {
                    IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                    AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                    Status = reader.GetString(reader.GetOrdinal("Status")),
                    Reason = reader.GetString(reader.GetOrdinal("Reason")),
                    PatientFullName = reader.GetString(reader.GetOrdinal("PatientFullName")),
                    PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail"))
                });
            }
            
            return Ok(appointments);
        }

        [HttpGet("{idAppointment}")]
        public async Task<IActionResult> GetAppointmentDetails(int idAppointment)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, a.InternalNotes, a.CreatedAt,
                       p.FirstName + ' ' + p.LastName AS PatientFullName, p.Email AS PatientEmail, p.PhoneNumber AS PatientPhoneNumber,
                       d.FirstName + ' ' + d.LastName AS DoctorFullName, d.LicenseNumber AS DoctorLicenseNumber
                FROM dbo.Appointments a
                JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
                JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
                WHERE a.IdAppointment = @IdAppointment;";

            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@IdAppointment", idAppointment);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return NotFound(new ErrorResponseDto { Message = "Appointment not found." });
            }

            var details = new AppointmentDetailsDto
            {
                IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                Reason = reader.GetString(reader.GetOrdinal("Reason")),
                InternalNotes = reader.IsDBNull(reader.GetOrdinal("InternalNotes")) ? null : reader.GetString(reader.GetOrdinal("InternalNotes")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                PatientFullName = reader.GetString(reader.GetOrdinal("PatientFullName")),
                PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail")),
                PatientPhoneNumber = reader.GetString(reader.GetOrdinal("PatientPhoneNumber")),
                DoctorFullName = reader.GetString(reader.GetOrdinal("DoctorFullName")),
                DoctorLicenseNumber = reader.GetString(reader.GetOrdinal("DoctorLicenseNumber"))
            };

            return Ok(details);
        }


        [HttpPost]
        public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto request)
        {
            if (request.AppointmentDate < DateTime.Now)
                return BadRequest(new ErrorResponseDto { Message = "Appointment date cannot be in the past." });

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var validateQuery = @"
                SELECT 
                    (SELECT IsActive FROM dbo.Patients WHERE IdPatient = @IdPatient) AS PatientActive,
                    (SELECT IsActive FROM dbo.Doctors WHERE IdDoctor = @IdDoctor) AS DoctorActive;";
            
            await using var validateCommand = new SqlCommand(validateQuery, connection);
            validateCommand.Parameters.AddWithValue("@IdPatient", request.IdPatient);
            validateCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);

            await using (var reader = await validateCommand.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    var patientActive = reader.IsDBNull(0) ? (bool?)null : reader.GetBoolean(0);
                    var doctorActive = reader.IsDBNull(1) ? (bool?)null : reader.GetBoolean(1);

                    if (patientActive == null || patientActive == false)
                        return BadRequest(new ErrorResponseDto { Message = "Patient does not exist or is inactive." });
                    if (doctorActive == null || doctorActive == false)
                        return BadRequest(new ErrorResponseDto { Message = "Doctor does not exist or is inactive." });
                }
            }

            var conflictQuery = "SELECT COUNT(1) FROM dbo.Appointments WHERE IdDoctor = @IdDoctor AND AppointmentDate = @AppointmentDate AND Status != 'Cancelled';";
            await using var conflictCommand = new SqlCommand(conflictQuery, connection);
            conflictCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
            conflictCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);

            var conflictCount = (int)(await conflictCommand.ExecuteScalarAsync() ?? 0);
            if (conflictCount > 0)
                return Conflict(new ErrorResponseDto { Message = "Doctor already has an appointment at this time." });

            var insertQuery = @"
                INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason, CreatedAt)
                OUTPUT INSERTED.IdAppointment
                VALUES (@IdPatient, @IdDoctor, @AppointmentDate, 'Scheduled', @Reason, SYSUTCDATETIME());";

            await using var insertCommand = new SqlCommand(insertQuery, connection);
            insertCommand.Parameters.AddWithValue("@IdPatient", request.IdPatient);
            insertCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
            insertCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);
            insertCommand.Parameters.AddWithValue("@Reason", request.Reason);

            var insertedId = (int)(await insertCommand.ExecuteScalarAsync() ?? 0);

            return CreatedAtAction(nameof(GetAppointmentDetails), new { idAppointment = insertedId }, new { IdAppointment = insertedId });
        }


        [HttpPut("{idAppointment}")]
        public async Task<IActionResult> UpdateAppointment(int idAppointment, [FromBody] UpdateAppointmentRequestDto request)
        {
            var validStatuses = new[] { "Scheduled", "Completed", "Cancelled" };
            if (!validStatuses.Contains(request.Status))
                return BadRequest(new ErrorResponseDto { Message = "Invalid status. Allowed values: Scheduled, Completed, Cancelled." });

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var checkQuery = "SELECT Status, AppointmentDate FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;";
            await using var checkCommand = new SqlCommand(checkQuery, connection);
            checkCommand.Parameters.AddWithValue("@IdAppointment", idAppointment);

            string currentStatus;
            DateTime currentDate;

            await using (var reader = await checkCommand.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync())
                    return NotFound(new ErrorResponseDto { Message = "Appointment not found." });

                currentStatus = reader.GetString(0);
                currentDate = reader.GetDateTime(1);
            }

            if (currentStatus == "Completed" && currentDate != request.AppointmentDate)
                return Conflict(new ErrorResponseDto { Message = "Cannot change the date of a completed appointment." });

            if (currentDate != request.AppointmentDate)
            {
                var conflictQuery = "SELECT COUNT(1) FROM dbo.Appointments WHERE IdDoctor = @IdDoctor AND AppointmentDate = @AppointmentDate AND IdAppointment != @IdAppointment AND Status != 'Cancelled';";
                await using var conflictCommand = new SqlCommand(conflictQuery, connection);
                conflictCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
                conflictCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);
                conflictCommand.Parameters.AddWithValue("@IdAppointment", idAppointment);

                var conflictCount = (int)(await conflictCommand.ExecuteScalarAsync() ?? 0);
                if (conflictCount > 0)
                    return Conflict(new ErrorResponseDto { Message = "Doctor already has an appointment at this new time." });
            }

            var updateQuery = @"
                UPDATE dbo.Appointments
                SET IdPatient = @IdPatient,
                    IdDoctor = @IdDoctor,
                    AppointmentDate = @AppointmentDate,
                    Status = @Status,
                    Reason = @Reason,
                    InternalNotes = @InternalNotes
                WHERE IdAppointment = @IdAppointment;";

            await using var updateCommand = new SqlCommand(updateQuery, connection);
            updateCommand.Parameters.AddWithValue("@IdAppointment", idAppointment);
            updateCommand.Parameters.AddWithValue("@IdPatient", request.IdPatient);
            updateCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
            updateCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);
            updateCommand.Parameters.AddWithValue("@Status", request.Status);
            updateCommand.Parameters.AddWithValue("@Reason", request.Reason);
            updateCommand.Parameters.AddWithValue("@InternalNotes", (object?)request.InternalNotes ?? DBNull.Value);

            try
            {
                await updateCommand.ExecuteNonQueryAsync();
            }
            catch (SqlException)
            {
                return BadRequest(new ErrorResponseDto { Message = "Failed to update appointment. Ensure patient and doctor exist and are active." });
            }

            return Ok();
        }

        // DELETE: api/appointments/{idAppointment}
        [HttpDelete("{idAppointment}")]
        public async Task<IActionResult> DeleteAppointment(int idAppointment)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var checkQuery = "SELECT Status FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;";
            await using var checkCommand = new SqlCommand(checkQuery, connection);
            checkCommand.Parameters.AddWithValue("@IdAppointment", idAppointment);

            var status = (string?)await checkCommand.ExecuteScalarAsync();

            if (status == null)
                return NotFound(new ErrorResponseDto { Message = "Appointment not found." });

            if (status == "Completed")
                return Conflict(new ErrorResponseDto { Message = "Cannot delete a completed appointment." });

            var deleteQuery = "DELETE FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;";
            await using var deleteCommand = new SqlCommand(deleteQuery, connection);
            deleteCommand.Parameters.AddWithValue("@IdAppointment", idAppointment);
            
            await deleteCommand.ExecuteNonQueryAsync();

            return NoContent();
        }
    }
}