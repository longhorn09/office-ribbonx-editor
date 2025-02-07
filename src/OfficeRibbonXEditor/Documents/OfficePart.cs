﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Packaging;
using System.Windows.Media.Imaging;
using System.Xml;

namespace OfficeRibbonXEditor.Documents
{
    public class OfficePart
    {
        private readonly string _id;

        public OfficePart(PackagePart part, XmlPart partType, string relationshipId)
        {
            Part = part;
            PartType = partType;
            _id = relationshipId;
            Name = Path.GetFileName(Part.Uri.ToString());
        }

        public PackagePart? Part { get; private set; }

        public XmlPart PartType { get; }

        public string Name { get; }

        public string ReadContent()
        {
            if (Part == null)
            {
                throw new InvalidOperationException($"Part was already removed");
            }

            var rd = new StreamReader(Part.GetStream(FileMode.Open, FileAccess.Read));
            var text = rd.ReadToEnd();
            rd.Close();
            return text;
        }
        
        public void Save(string text)
        {
            if (Part == null)
            {
                throw new InvalidOperationException($"Part was already removed");
            }

            if (text == null)
            {
                Debug.Print("Trying to save a null string");
                return;
            }

            using (var tw = new StreamWriter(Part.GetStream(FileMode.Create, FileAccess.Write)))
            {
                tw.Write(text);
            }
        }

        public Dictionary<string, BitmapImage> GetImages()
        {
            if (Part == null)
            {
                throw new InvalidOperationException($"Part was already removed");
            }

            var imageCollection = new Dictionary<string, BitmapImage>();

            foreach (var relationship in Part.GetRelationshipsByType(OfficeDocument.ImagePartRelType))
            {
                var customImageUri = PackUriHelper.ResolvePartUri(relationship.SourceUri, relationship.TargetUri);
                if (!Part.Package.PartExists(customImageUri))
                {
                    continue;
                }

                var imagePart = Part.Package.GetPart(customImageUri);

                var imageStream = imagePart.GetStream(FileMode.Open, FileAccess.Read);

                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = imageStream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();

                imageCollection.Add(relationship.Id, image);
                
                imageStream.Close();
            }

            return imageCollection;
        }

        public string? AddImage(string filePath, string? imageId, Func<string?, string?, bool>? alreadyExistingAction = null)
        {
            if (PartType != XmlPart.RibbonX12 && PartType != XmlPart.RibbonX14)
            {
                throw new NotSupportedException($"The part type must be either RibbonX12 or RibbonX14, not {PartType}");
            }

            if (filePath == null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            if (filePath.Length == 0)
            {
                throw new ArgumentException("File path cannot be empty");
            }

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (imageId == null)
            {
                imageId = XmlConvert.EncodeName(fileName);
            }

            if (imageId == null)
            {
                throw new ArgumentNullException(nameof(imageId));
            }

            if (imageId.Length == 0)
            {
                throw new ArgumentException("Target Id cannot be empty");
            }

            return AddImageHelper(filePath, imageId, alreadyExistingAction);
        }
        
        public void RemoveImage(string imageId)
        {
            if (Part == null)
            {
                throw new InvalidOperationException("Part was already removed");
            }

            if (imageId == null)
            {
                throw new ArgumentNullException(nameof(imageId));
            }

            if (imageId.Length == 0)
            {
                return;
            }

            if (!Part.RelationshipExists(imageId))
            {
                return;
            }

            var imageRel = Part.GetRelationship(imageId);

            var imageUri = PackUriHelper.ResolvePartUri(imageRel.SourceUri, imageRel.TargetUri);
            if (Part.Package.PartExists(imageUri))
            {
                Part.Package.DeletePart(imageUri);
            }

            Part.DeleteRelationship(imageId);
        }

        public void Remove()
        {
            if (Part == null)
            {
                throw new InvalidOperationException($"Part was already removed");
            }

            // Remove all image parts first
            foreach (var relationship in Part.GetRelationships())
            {
                var relUri = PackUriHelper.ResolvePartUri(relationship.SourceUri, relationship.TargetUri);
                if (Part.Package.PartExists(relUri))
                {
                    Part.Package.DeletePart(relUri);
                }
            }

            Part.Package.DeleteRelationship(_id);
            Part.Package.DeletePart(Part.Uri);

            Part = null;
        }

        public void ChangeImageId(string source, string target)
        {
            if (Part == null)
            {
                throw new InvalidOperationException("Part was already removed");
            }

            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (target.Length == 0)
            {
                throw new ArgumentException("Target Id cannot be empty");
            }

            if (source == target)
            {
                return;
            }

            if (!Part.RelationshipExists(source))
            {
                return;
            }

            if (Part.RelationshipExists(target))
            {
                throw new InvalidOperationException($"OOXML part Id \"{target}\" already exists");
            }

            var imageRel = Part.GetRelationship(source);

            // Find the new Uri for the icon (TODO: PNG assumed)
            var originalUri = PackUriHelper.ResolvePartUri(imageRel.SourceUri, imageRel.TargetUri);
            var newRelativeUri = FindFirstAvailableImageUri(Part, target, ".png");
            var newUri = PackUriHelper.ResolvePartUri(imageRel.SourceUri, newRelativeUri);

            // Create the new package part
            var originalPart = Part.Package.GetPart(originalUri);
            var newPart = Part.Package.CreatePart(newUri, originalPart.ContentType, originalPart.CompressionOption);
            if (newPart == null)
            {
                throw new InvalidOperationException("There is no file associated with the icon you are trying to rename");
            }

            using (var br = new BinaryReader(originalPart.GetStream(FileMode.Open, FileAccess.Read)))
            using (var bw = new BinaryWriter(newPart.GetStream(FileMode.Create, FileAccess.Write)))
            {
                var buffer = new byte[1024];
                int byteCount;
                while ((byteCount = br.Read(buffer, 0, buffer.Length)) > 0)
                {
                    bw.Write(buffer, 0, byteCount);
                }

                bw.Flush();
            }

            Part.Package.DeletePart(originalUri);
            Part.DeleteRelationship(source);

            Part.CreateRelationship(newRelativeUri, imageRel.TargetMode, imageRel.RelationshipType, target);
        }

        private static string MapImageContentType(string extension)
        {
            if (extension == null)
            {
                throw new ArgumentNullException(nameof(extension));
            }

            if (extension.Length == 0)
            {
                throw new ArgumentException("Extension cannot be empty.");
            }

            var extLowerCase = extension.ToUpperInvariant();

            switch (extLowerCase)
            {
                case "JPG":
                    return "image/jpeg";
                default:
                    return "image/" + extLowerCase;
            }
        }

        private string? AddImageHelper(string fileName, string imageId, Func<string?, string?, bool>? alreadyExistingAction = null)
        {
            if (Part == null)
            {
                throw new InvalidOperationException($"Part was already removed");
            }

            if (fileName == null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            Debug.Assert(File.Exists(fileName), fileName + " does not exist.");
            if (!File.Exists(fileName))
            {
                return null;
            }

            var extension = Path.GetExtension(fileName);

            // Check for duplicates and correct ID if necessary
            var originalId = imageId;
            imageId = FindFirstAvailableImageId(Part, imageId);

            if (imageId != originalId && !(alreadyExistingAction?.Invoke(originalId, imageId) ?? true))
            {
                return null;
            }

            // Now do the same for the Uri (which does need to coincide with the ID)
            var imageUri = FindFirstAvailableImageUri(Part, originalId, extension);

            var imageRel = Part.CreateRelationship(imageUri, TargetMode.Internal, OfficeDocument.ImagePartRelType, imageId);

            var imagePart = Part.Package.CreatePart(
                PackUriHelper.ResolvePartUri(imageRel.SourceUri, imageRel.TargetUri),
                MapImageContentType(Path.GetExtension(fileName)));

            if (imagePart == null)
            {
                Debug.Print("Fail to create image part.");
                return null;
            }

            using var br = new BinaryReader(File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            using var bw = new BinaryWriter(imagePart.GetStream(FileMode.Create, FileAccess.Write));
            
            var buffer = new byte[1024];
            int byteCount;
            while ((byteCount = br.Read(buffer, 0, buffer.Length)) > 0)
            {
                bw.Write(buffer, 0, byteCount);
            }

            bw.Flush();

            return imageRel.Id;
        }

        private static string FindFirstAvailableImageId(PackagePart part, string imageId)
        {
            var index = 0;
            var renamed = imageId;
            while (true)
            {
                if (!part.RelationshipExists(renamed))
                {
                    return renamed;
                }

                Debug.Write($"A relationship '{renamed}' already exists");
                renamed = $"{imageId}{index++}";
            }
        }

        private static Uri FindFirstAvailableImageUri(PackagePart part, string imageId, string extension)
        {
            var imageUri = new Uri($"images/{imageId}{extension}", UriKind.Relative);
            var index = 0;
            while (true)
            {
                if (!part.Package.PartExists(PackUriHelper.ResolvePartUri(part.Uri, imageUri)))
                {
                    return imageUri;
                }

                Debug.Write($"A Uri '{imageUri}' already exists");
                imageUri = new Uri($"images/{imageId}{index++}{extension}", UriKind.Relative);
            }
        }
    }
}