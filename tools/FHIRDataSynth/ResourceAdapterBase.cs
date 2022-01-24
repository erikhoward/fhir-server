﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

namespace ResourceProcessorNamespace
{
    internal class ResourceProcessorException : Exception
    {
        public ResourceProcessorException(string resourceGroupDir, string resourceName, string resourceId, string message)
            : base($"{resourceGroupDir}/{resourceName}/{resourceId}: {message}")
        {
        }

        public ResourceProcessorException(string resourceGroupDir, string resourceRef, string message)
            : base($"{resourceGroupDir}/{resourceRef}: {message}")
        {
        }
    }

    internal struct ResourceSiblingsContainer<T>
        where T : struct
    {
        private T[] siblings;

        public ResourceSiblingsContainer(T[] siblings)
        {
            this.siblings = siblings;
        }

        public ref T Get(int siblingNumber, string resourceGroupDir, string resourceName, string resourceId)
        {
            if (siblingNumber >= Count)
            {
                throw new ResourceProcessorException(resourceGroupDir, resourceName, resourceId, "Sibling array index too big.");
            }

            return ref siblings[siblingNumber];
        }

        public int Count { get => siblings.Length; }

        public ref T GetOriginal()
        {
            return ref siblings[0]; // There is always at least one and first one is always original sibling.
        }
    }

    internal abstract class ResourceAdapter<T, TS>
        where T : class
        where TS : struct
    {
        protected ResourceGroupProcessor processor;
        protected JsonSerializerOptions options;

        public void Initialize(ResourceGroupProcessor processor, JsonSerializerOptions options)
        {
            this.processor = processor;
            this.options = options;
        }

        public abstract TS CreateOriginal(ResourceGroupProcessor processor, T json);

        public abstract string GetId(T json);

        public virtual void SetId(T json, string id, ResourceGroupProcessor processor)
        {
            throw new NotImplementedException($"SetId called on resource other than '{ResourceGroupProcessor.OrganizationStr}'");
        }

        public abstract string GetResourceType(T json);

        public abstract TS CreateClone(ResourceGroupProcessor processor, T originalJson, T cloneJson, int refSiblingNumber); // WARNING! originalJson MUST not be modified, member classes of originalJson MUST not be asigned to cloneJson!

        public abstract bool ValidateResourceRefsAndSelect(ResourceGroupProcessor processor, T json, out bool select);

        public int GetRefSiblingNumberLimit(ResourceGroupProcessor processor, T originalJson)
        {
            int refSiblingNumberLimit = int.MaxValue;
            IterateReferences(false, processor, originalJson, originalJson, -1, ref refSiblingNumberLimit);
            return refSiblingNumberLimit;
        }

        protected abstract void IterateReferences(bool clone, ResourceGroupProcessor processor, T originalJson, T cloneJson, int refSiblingNumber, ref int refSiblingNumberLimit);

        protected string CloneOrLimit(bool clone, T originalJson, string originalReference, int refSiblingNumber, ref int refSiblingNumberLimit)
        {
            string rgd = processor.GetResourceGroupDir();
            string rt = GetResourceType(originalJson);
            string id = GetId(originalJson);

            int index = originalReference.IndexOf('/', StringComparison.Ordinal);
            if (index < 0)
            {
                throw new ResourceProcessorException(rgd, rt, id, $"Invalid reference {originalReference} in CloneOrLimit()."); // Should never happen!
            }

            string refType = originalReference.Substring(0, index);
            string refId = originalReference.Substring(index + 1);

            switch (refType)
            {
                /*case "AllergyIntolerance":
                    {
                    }
                case "CarePlan":
                    {
                    }*/
                case "CareTeam":
                    {
                        if (clone)
                        {
                            return $"{refType}/{processor.careTeams[refId].Get(refSiblingNumber, rgd, rt, id).id}";
                        }
                        else
                        {
                            refSiblingNumberLimit = Math.Min(processor.careTeams[refId].Count, refSiblingNumberLimit);
                        }

                        return originalReference;
                    }

                case "Claim":
                    {
                        if (clone)
                        {
                            return $"{refType}/{processor.claims[refId].Get(refSiblingNumber, rgd, rt, id).id}";
                        }
                        else
                        {
                            refSiblingNumberLimit = Math.Min(processor.claims[refId].Count, refSiblingNumberLimit);
                        }

                        return originalReference;
                    }

                /*case "Condition":
                    {
                    }
                case "Device":
                    {
                    }
                case "DiagnosticReport":
                    {
                    }*/
                case "Encounter":
                    {
                        if (clone)
                        {
                            return $"{refType}/{processor.encounters[refId].Get(refSiblingNumber, rgd, rt, id).id}";
                        }
                        else
                        {
                            refSiblingNumberLimit = Math.Min(processor.encounters[refId].Count, refSiblingNumberLimit);
                        }

                        return originalReference;
                    }

                /*case "ExplanationOfBenefits":
                    {
                    }
                case "ImagingStudy":
                    {
                    }
                case "Immunization":
                    {
                    }
                case "MedicationAdministration":
                    {
                    }*/
                case "MedicationRequest":
                    {
                        if (clone)
                        {
                            return $"{refType}/{processor.medicationRequests[refId].Get(refSiblingNumber, rgd, rt, id).id}";
                        }
                        else
                        {
                            refSiblingNumberLimit = Math.Min(processor.medicationRequests[refId].Count, refSiblingNumberLimit);
                        }

                        return originalReference;
                    }

                case "Observation":
                    {
                        if (clone)
                        {
                            return $"{refType}/{processor.observations[refId].Get(refSiblingNumber, rgd, rt, id).id}";
                        }
                        else
                        {
                            refSiblingNumberLimit = Math.Min(processor.observations[refId].Count, refSiblingNumberLimit);
                        }

                        return originalReference;
                    }

                case "Organization":
                    {
                        if (clone)
                        {
                            return $"{refType}/{processor.organizations[refId].Get(refSiblingNumber, rgd, rt, id).id}";
                        }
                        else
                        {
                            refSiblingNumberLimit = Math.Min(processor.organizations[refId].Count, refSiblingNumberLimit);
                        }

                        return originalReference;
                    }

                case "Patient":
                    {
                        if (clone)
                        {
                            return $"{refType}/{processor.patients[refId].Get(refSiblingNumber, rgd, rt, id).id}";
                        }
                        else
                        {
                            refSiblingNumberLimit = Math.Min(processor.patients[refId].Count, refSiblingNumberLimit);
                        }

                        return originalReference;
                    }

                case "Practitioner":
                    {
                        if (clone)
                        {
                            return $"{refType}/{processor.practitioners[refId].Get(refSiblingNumber, rgd, rt, id).id}";
                        }
                        else
                        {
                            refSiblingNumberLimit = Math.Min(processor.practitioners[refId].Count, refSiblingNumberLimit);
                        }

                        return originalReference;
                    }

                /*case "Procedure":
                    {
                    }
                case "SupplyDelivery":
                    {
                    }*/
                default: throw new ResourceProcessorException(rgd, rt, id, $"Invalid reference type {originalReference} in CloneOrLimit()."); // Should never happen!
            }
        }

        // Enumerator.
        public virtual IEnumerator<EnumeratorItem> GetEnumerator()
        {
            throw new NotImplementedException();
        }

        public class EnumeratorItem
        {
            public int size;
            public T json;
        }

        public abstract class EnumeratorBase<TS1> : IEnumerator<EnumeratorItem>
        {
            protected abstract T LoadFHIRExampleFile();

            protected abstract void InitializeFHIRExample(T json, TS1 initializer);

            private ResourceGroupProcessor processor;
            private JsonSerializerOptions options;
            private EnumeratorItem currentItem;
            private string line;

            public EnumeratorBase(ResourceGroupProcessor processor, JsonSerializerOptions options)
            {
                this.processor = processor;
                this.options = options;
                currentItem = new EnumeratorItem();
            }

            protected abstract bool InitializerMoveNext();

            protected abstract TS1 InitializerCurrent { get; }

            public bool MoveNext()
            {
                if (!InitializerMoveNext())
                {
                    return false;
                }
                else
                {
                    TS1 initializer = InitializerCurrent;
                    if (currentItem.json == null)
                    {
                        currentItem.json = LoadFHIRExampleFile();
                        InitializeFHIRExample(currentItem.json, initializer);
                        line = JsonSerializer.Serialize(currentItem.json, options);
                        currentItem.size = line.Length;
                    }
                    else
                    {
                        currentItem.json = JsonSerializer.Deserialize<T>(line); // TODO: optimization, remove this deserialization by processing json item one at the time instead of loading them all into array.
                        InitializeFHIRExample(currentItem.json, initializer);
                    }
                }

                return true;
            }

            public abstract void Reset();

            public abstract void Dispose();

            public EnumeratorItem Current
            {
                get { return currentItem; }
            }

            object IEnumerator.Current
            {
                get { return Current; }
            }
        }
    }
}