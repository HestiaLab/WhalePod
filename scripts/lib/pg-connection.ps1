# Shared Npgsql-style connection-string parsing for libpq tools.
# Uses DbConnectionStringBuilder so quoted semicolons/spaces are preserved.

function Get-PgConnectionSettings {
    param([Parameter(Mandatory = $true)][string]$ConnectionString)

    $builder = New-Object System.Data.Common.DbConnectionStringBuilder
    $builder.set_ConnectionString($ConnectionString)
    $settings = @{}
    foreach ($key in $builder.Keys) {
        $normalized = ([string]$key).ToLowerInvariant() -replace '\s', ''
        $settings[$normalized] = [string]$builder[$key]
    }

    return $settings
}

function Convert-PgSslMode {
    param([string]$Value)
    switch ($Value.ToLowerInvariant()) {
        'verifyca' { return 'verify-ca' }
        'verify-ca' { return 'verify-ca' }
        'verifyfull' { return 'verify-full' }
        'verify-full' { return 'verify-full' }
        'disable' { return 'disable' }
        'allow' { return 'allow' }
        'prefer' { return 'prefer' }
        'require' { return 'require' }
        default { return $Value }
    }
}

function Get-PgConnectionEnvironment {
    param([hashtable]$Settings)

    $hostValue = '127.0.0.1'
    if ($Settings.ContainsKey('host') -and -not [string]::IsNullOrWhiteSpace($Settings['host'])) {
        $hostValue = $Settings['host']
    }

    $portValue = '5432'
    if ($Settings.ContainsKey('port') -and -not [string]::IsNullOrWhiteSpace($Settings['port'])) {
        $portValue = $Settings['port']
    }

    $values = [ordered]@{
        PGHOST = $hostValue
        PGPORT = $portValue
    }
    if ($Settings.ContainsKey('username')) { $values['PGUSER'] = $Settings['username'] }
    if ($Settings.ContainsKey('password')) { $values['PGPASSWORD'] = $Settings['password'] }
    if ($Settings.ContainsKey('sslmode')) {
        $values['PGSSLMODE'] = Convert-PgSslMode $Settings['sslmode']
    }
    if ($Settings.ContainsKey('rootcertificate')) { $values['PGSSLROOTCERT'] = $Settings['rootcertificate'] }
    elseif ($Settings.ContainsKey('sslrootcert')) { $values['PGSSLROOTCERT'] = $Settings['sslrootcert'] }
    if ($Settings.ContainsKey('sslcertificate')) { $values['PGSSLCERT'] = $Settings['sslcertificate'] }
    elseif ($Settings.ContainsKey('sslcert')) { $values['PGSSLCERT'] = $Settings['sslcert'] }
    elseif ($Settings.ContainsKey('clientcertificate')) { $values['PGSSLCERT'] = $Settings['clientcertificate'] }
    if ($Settings.ContainsKey('sslkey')) { $values['PGSSLKEY'] = $Settings['sslkey'] }
    elseif ($Settings.ContainsKey('clientkey')) { $values['PGSSLKEY'] = $Settings['clientkey'] }
    elseif ($Settings.ContainsKey('clientcertificatekey')) { $values['PGSSLKEY'] = $Settings['clientcertificatekey'] }
    if ($Settings.ContainsKey('sslpassword')) { $values['PGSSLPASSWORD'] = $Settings['sslpassword'] }

    return $values
}

function Set-PgConnectionEnvironment {
    param([hashtable]$Settings)

    $values = Get-PgConnectionEnvironment $Settings
    foreach ($pair in $values.GetEnumerator()) {
        Set-Item -Path "Env:$($pair.Key)" -Value $pair.Value
    }

    return $values
}

function Clear-PgConnectionSecrets {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:PGSSLPASSWORD -ErrorAction SilentlyContinue
}
