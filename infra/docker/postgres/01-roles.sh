#!/bin/sh
# Se ejecuta UNA vez al crear el volumen de datos. Roles separados: migrador (dueño del esquema, DDL) y runtime (solo DML).
# Las contraseñas se leen con \set desde archivos de secretos: no aparecen en argumentos de procesos ni en logs.
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\set mig_pw `cat /run/secrets/db_migrator_password`
\set app_pw `cat /run/secrets/db_app_password`
CREATE ROLE alvarez_migrator LOGIN PASSWORD :'mig_pw';
CREATE ROLE alvarez_app LOGIN PASSWORD :'app_pw' NOCREATEDB NOCREATEROLE NOINHERIT;
REVOKE ALL ON DATABASE alvarez FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE alvarez TO alvarez_migrator;
GRANT CONNECT ON DATABASE alvarez TO alvarez_app;
ALTER SCHEMA public OWNER TO alvarez_migrator;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO alvarez_app;
ALTER DEFAULT PRIVILEGES FOR ROLE alvarez_migrator IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO alvarez_app;
ALTER DEFAULT PRIVILEGES FOR ROLE alvarez_migrator IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO alvarez_app;
SQL
echo "Roles alvarez_migrator y alvarez_app creados."
