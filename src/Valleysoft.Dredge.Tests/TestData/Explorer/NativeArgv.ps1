[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::Write((ConvertTo-Json -InputObject ([string[]]$args) -Compress))
