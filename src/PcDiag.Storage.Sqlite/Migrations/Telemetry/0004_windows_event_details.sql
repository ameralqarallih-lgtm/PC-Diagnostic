-- Telemetry schema v4 (Phase 7): Windows event details.
-- proc_key links an application crash or hang to ProcessIdentity (when the event names the process start time),
-- service_name links an SCM event to ServiceEvents, and data_json holds the fields the rule extracted
-- (e.g. exception code, faulting module, bug check code).
ALTER TABLE WindowsEvents ADD COLUMN proc_key INTEGER;
ALTER TABLE WindowsEvents ADD COLUMN service_name TEXT;
ALTER TABLE WindowsEvents ADD COLUMN data_json TEXT;

CREATE INDEX IX_ProcessEvents_proc_key ON ProcessEvents(proc_key) WHERE proc_key IS NOT NULL;
