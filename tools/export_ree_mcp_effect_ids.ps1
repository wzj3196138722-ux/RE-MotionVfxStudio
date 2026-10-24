param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$FixedOutputPath
)

$stream = [IO.File]::OpenRead($AssemblyPath)
try {
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    $rows = [Collections.Generic.List[object]]::new()
    $fixedRows = [Collections.Generic.List[object]]::new()
    foreach ($handle in $metadata.TypeDefinitions) {
        $definition = $metadata.GetTypeDefinition($handle)
        if ($metadata.GetString($definition.Name) -ne 'EffectID_Common' -or
            $metadata.GetString($definition.Namespace) -ne 'app') { continue }
        foreach ($nestedHandle in $definition.GetNestedTypes()) {
            $nested = $metadata.GetTypeDefinition($nestedHandle)
            $nestedName = $metadata.GetString($nested.Name)
            if ($nestedName -ne 'ID' -and $nestedName -ne 'ELEMENT_ID_Fixed') { continue }
            foreach ($fieldHandle in $nested.GetFields()) {
                $field = $metadata.GetFieldDefinition($fieldHandle)
                $constantHandle = $field.GetDefaultValue()
                if ($constantHandle.IsNil) { continue }
                $constant = $metadata.GetConstant($constantHandle)
                if ($constant.TypeCode -ne [Reflection.Metadata.ConstantTypeCode]::Int32) { continue }
                $reader = $metadata.GetBlobReader($constant.Value)
                $value = $reader.ReadInt32()
                $row = [pscustomobject]@{
                    Name = $metadata.GetString($field.Name)
                    Value = $value
                    Hex = '0x{0:X}' -f [BitConverter]::ToUInt32([BitConverter]::GetBytes([int32]$value), 0)
                }
                if ($nestedName -eq 'ID') { $rows.Add($row) }
                else { $fixedRows.Add($row) }
            }
        }
        break
    }
    $rows | Export-Csv -LiteralPath $OutputPath -Encoding utf8 -NoTypeInformation
    $fixedRows | Export-Csv -LiteralPath $FixedOutputPath -Encoding utf8 -NoTypeInformation
    Write-Output "Exported $($rows.Count) EffectID_Common.ID constants to $OutputPath"
    Write-Output "Exported $($fixedRows.Count) EffectID_Common.ELEMENT_ID_Fixed constants to $FixedOutputPath"
} finally {
    $stream.Dispose()
}
