using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Calibre
{
    public class CalibreImportSettingsValidator : AbstractValidator<CalibreImportSettings>
    {
        public CalibreImportSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).NotEmpty();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
        }
    }

    public class CalibreImportSettings : IImportListSettings
    {
        private static readonly CalibreImportSettingsValidator Validator = new CalibreImportSettingsValidator();

        public CalibreImportSettings()
        {
            BaseUrl = "localhost";
            Port = 8081;
            Library = "Calibre_Library";
        }

        [FieldDefinition(0, Label = "Calibre Host", HelpText = "Hostname or IP of Calibre Content Server")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Number, HelpText = "Calibre Content Server port (default 8081)")]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "URL Base", HelpText = "Adds a prefix to the calibre API url, e.g. http://[host]:[port]/[urlbase]/api", Advanced = true)]
        public string UrlBase { get; set; }

        [FieldDefinition(3, Label = "Username", HelpText = "Calibre Content Server username", Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(4, Label = "Password", Type = FieldType.Password, HelpText = "Calibre Content Server password", Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(5, Label = "Library", HelpText = "Calibre library name")]
        public string Library { get; set; }

        [FieldDefinition(6, Label = "Use SSL", Type = FieldType.Checkbox, HelpText = "Connect to Calibre over HTTPS", Advanced = true)]
        public bool UseSsl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
