using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class ObjectInspector
    {
        private readonly OutputWriter _writer;

        public ObjectInspector(OutputWriter writer)
        {
            _writer = writer;
        }

        public void InspectObject(ClrHeap heap, ulong address)
        {
            _writer.WriteHeader($"OBJECT INSPECTION: 0x{address:X}");

            ClrObject obj = heap.GetObject(address);

            if (!obj.IsValid)
            {
                _writer.WriteLine($"Object at 0x{address:X} is not valid or not found.");
                return;
            }

            PrintObjectDetails(obj, heap);
            PrintFieldValues(obj);
            PrintReferences(obj, heap);
            PrintReferrers(obj, heap);

            _writer.WriteLine(StringConstants.Equals80);
        }

        private void PrintObjectDetails(ClrObject obj, ClrHeap heap)
        {
            _writer.WriteLine("\nOBJECT DETAILS:");
            _writer.WriteSeparator();
            _writer.WriteLine($"Address:     0x{obj.Address:X}");
            _writer.WriteLine($"Type:        {obj.Type?.Name ?? StringConstants.UnknownType}");
            _writer.WriteLine($"Size:        {FormatHelper.FormatBytes(obj.Size)}");
            _writer.WriteLine($"Is Array:    {obj.IsArray}");

            if (obj.IsArray)
            {
                _writer.WriteLine($"Array Length: {obj.AsArray().Length:N0}");
                _writer.WriteLine($"Element Type: {obj.Type?.ComponentType?.Name ?? "Unknown"}");
            }

            // Check if rooted
            bool isRooted = false;
            string rootKind = "Not Rooted";
            foreach (var root in heap.EnumerateRoots())
            {
                if (root.Object.Address == obj.Address)
                {
                    isRooted = true;
                    rootKind = root.RootKind.ToString();
                    break;
                }
            }
            _writer.WriteLine($"Rooted:      {(isRooted ? $"Yes ({rootKind})" : "No")}");
        }

        private void PrintFieldValues(ClrObject obj)
        {
            if (obj.Type == null)
                return;

            _writer.WriteLine("\nFIELDS:");
            _writer.WriteSeparator();

            var fields = obj.Type.Fields.ToList();
            if (fields.Count == 0)
            {
                _writer.WriteLine("  (No fields)");
                return;
            }

            foreach (var field in fields.Take(50))
            {
                string fieldName = field.Name ?? "?";
                string fieldType = field.Type?.Name ?? "?";

                try
                {
                    if (field.IsValueType)
                    {
                        if (fieldType == "System.Int32")
                        {
                            int value = field.Read<int>(obj, interior: false);
                            _writer.WriteLine($"  {fieldType,-40} {fieldName,-30} = {value}");
                        }
                        else if (fieldType == "System.Int64")
                        {
                            long value = field.Read<long>(obj, interior: false);
                            _writer.WriteLine($"  {fieldType,-40} {fieldName,-30} = {value}");
                        }
                        else if (fieldType == "System.Boolean")
                        {
                            bool value = field.Read<bool>(obj, interior: false);
                            _writer.WriteLine($"  {fieldType,-40} {fieldName,-30} = {value}");
                        }
                        else if (fieldType == "System.Double")
                        {
                            double value = field.Read<double>(obj, interior: false);
                            _writer.WriteLine($"  {fieldType,-40} {fieldName,-30} = {value:F2}");
                        }
                        else
                        {
                            _writer.WriteLine($"  {fieldType,-40} {fieldName,-30} = <value type>");
                        }
                    }
                    else if (field.IsObjectReference)
                    {
                        ClrObject refObj = field.ReadObject(obj, interior: false);
                        if (refObj.IsValid)
                        {
                            if (refObj.Type?.Name == "System.String")
                            {
                                string? strValue = refObj.AsString();
                                string display = strValue != null && strValue.Length > 40
                                    ? $"\"{strValue.Substring(0, 37)}...\""
                                    : $"\"{strValue}\"";
                                _writer.WriteLine($"  {fieldType,-40} {fieldName,-30} = {display}");
                            }
                            else
                            {
                                _writer.WriteLine($"  {fieldType,-40} {fieldName,-30} = 0x{refObj.Address:X}");
                            }
                        }
                        else
                        {
                            _writer.WriteLine($"  {fieldType,-40} {fieldName,-30} = null");
                        }
                    }
                }
                catch
                {
                    _writer.WriteLine($"  {fieldType,-40} {fieldName,-30} = <error reading>");
                }
            }

            if (fields.Count > 50)
            {
                _writer.WriteLine($"\n  ... and {fields.Count - 50} more fields");
            }
        }

        private void PrintReferences(ClrObject obj, ClrHeap heap)
        {
            _writer.WriteLine("\nOUTGOING REFERENCES (this object points to):");
            _writer.WriteSeparator();

            var references = obj.EnumerateReferences(carefully: true).Take(20).ToList();
            if (references.Count == 0)
            {
                _writer.WriteLine("  (No references)");
                return;
            }

            foreach (var reference in references)
            {
                if (reference.IsValid && reference.Type != null)
                {
                    _writer.WriteLine($"  → {reference.Type.Name,-50} @ 0x{reference.Address:X} ({FormatHelper.FormatBytes(reference.Size)})");
                }
            }

            int totalRefs = obj.EnumerateReferences(carefully: true).Count();
            if (totalRefs > 20)
            {
                _writer.WriteLine($"\n  ... and {totalRefs - 20} more references");
            }
        }

        private void PrintReferrers(ClrObject obj, ClrHeap heap)
        {
            _writer.WriteLine("\nINCOMING REFERENCES (objects pointing to this):");
            _writer.WriteSeparator();

            var referrers = new List<ClrObject>();
            int referrerCount = 0;
            const int MaxReferrers = 20;

            foreach (ClrObject potentialReferrer in heap.EnumerateObjects())
            {
                if (!potentialReferrer.IsValid)
                    continue;

                foreach (var reference in potentialReferrer.EnumerateReferences(carefully: true))
                {
                    if (reference.Address == obj.Address)
                    {
                        referrerCount++;
                        if (referrers.Count < MaxReferrers)
                        {
                            referrers.Add(potentialReferrer);
                        }
                        break;
                    }
                }

                if (referrers.Count >= MaxReferrers)
                    break;
            }

            if (referrerCount == 0)
            {
                _writer.WriteLine("  (No incoming references - may be eligible for GC)");
                return;
            }

            _writer.WriteLine($"Total Referrers: {referrerCount:N0}\n");

            foreach (var referrer in referrers)
            {
                if (referrer.Type != null)
                {
                    _writer.WriteLine($"  ← {referrer.Type.Name,-50} @ 0x{referrer.Address:X}");
                }
            }

            if (referrerCount > MaxReferrers)
            {
                _writer.WriteLine($"\n  ... and {referrerCount - MaxReferrers} more referrers");
            }
        }
    }
}
