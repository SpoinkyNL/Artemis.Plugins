﻿using Artemis.Core.DataModelExpansions;
using Artemis.Plugins.DataModelExpansions.Aida64.DataModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Artemis.Plugins.DataModelExpansions.Aida64
{
    public class PluginDataModelExpansion : DataModelExpansion<Aida64DataModel>
    {
        private const string SHARED_MEMORY = "AIDA64_SensorValues";

        private MemoryMappedFile memoryMappedFile;
        private MemoryMappedViewStream rootStream;
        private List<AidaElement> _aidaElements;

        public override void Enable()
        {
            try
            {
                memoryMappedFile = MemoryMappedFile.OpenExisting(SHARED_MEMORY, MemoryMappedFileRights.Read);
                rootStream = memoryMappedFile.CreateViewStream(
                    0,
                    0,
                    MemoryMappedFileAccess.Read);
                _aidaElements = new List<AidaElement>();
            }
            catch
            {
                //log failure, etc
                throw;
            }

            AddTimedUpdate(TimeSpan.FromSeconds(1), UpdateSensors);
        }

        public override void Update(double deltaTime)
        {
        }

        public override void Disable()
        {
            memoryMappedFile?.Dispose();
            rootStream?.Dispose();
        }

        private void UpdateSensors(double deltaTime)
        {
            ReadAidaSensors();
            UpdateDataModels();
        }

        private void UpdateDataModels()
        {
            foreach (var item in _aidaElements)
            {
                AidaElementDataModel dm = DataModel.DynamicChild<AidaElementDataModel>(item.Id)
                    ?? DataModel.AddDynamicChild(new AidaElementDataModel(), item.Id, item.Label);

                dm.Value = item.Value;
            }
        }

        private void ReadAidaSensors()
        {
            XmlDocument doc = new();
            doc.LoadXml(ReadMemoryMappedFile());
            foreach (XmlElement item in doc["aida"])
            {
                switch (item.Name)
                {
                    //case "sys":
                    //    break;
                    //case "fan":
                    //    break;
                    //case "temp":
                    //    break;
                    //case "volt":
                    //    break;
                    //case "pwr":
                    //    break;
                    //case "curr":
                    //    break;
                    //case "duty":
                    //    break;

                    default:
                        _aidaElements.Add(AidaElement.FromXmlElement(item));
                        break;
                }
            }
        }

        private string ReadMemoryMappedFile()
        {
            rootStream.Seek(0, SeekOrigin.Begin);

            var sb = new StringBuilder();
            //since there's no root element we have to add one ourselves
            sb.Append("<aida>");

            int c;
            while ((c = rootStream.ReadByte()) > 0)
                sb.Append((char)c);

            sb.Append("</aida>");

            return sb.ToString();
        }
    }
}