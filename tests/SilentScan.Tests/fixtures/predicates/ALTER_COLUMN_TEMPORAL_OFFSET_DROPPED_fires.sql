-- Oracle-confirmed against the standing Docker instance: ALTER COLUMN retyping a DATETIMEOFFSET
-- column into DATETIME2 succeeds with no error and silently drops the UTC offset, keeping the
-- local date/time digits unchanged rather than normalizing the value to UTC first.
CREATE TABLE dbo.Appointment
(
    AppointmentId INT            NOT NULL PRIMARY KEY,
    ScheduledAt   DATETIMEOFFSET NOT NULL
);
GO
ALTER TABLE dbo.Appointment ALTER COLUMN ScheduledAt DATETIME2;
