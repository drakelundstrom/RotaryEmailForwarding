using System.Globalization;
using System.Net;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Routing;
using RotaryEmailForwarding.FunctionApp.Services;

namespace RotaryEmailForwarding.FunctionApp.Email;

public sealed class EmailTemplateService(AppConfiguration configuration)
{
    private enum EmailLanguage
    {
        English,
        French,
        Spanish
    }

    private const string PublicSiteUrl = "https://studyabroadscholarships.org/";
    private const string PublicSiteDisplayName = "studyabroadscholarships.org";

    public OutboundEmailMessage BuildMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        return route.Kind switch
        {
            SubmissionRouteKind.District => BuildDistrictForwardingMessage(submission, route),
            SubmissionRouteKind.Country => BuildCountryForwardingMessage(submission, route),
            SubmissionRouteKind.UncertifiedCountry => BuildManualRoutingMessage(submission, route),
            _ => BuildManualRoutingMessage(submission, route)
        };
    }

    public OutboundEmailMessage BuildOperatorFailureMessage(
        string correlationId,
        string failureSummary,
        string rawSubmissionJson)
    {
        return new OutboundEmailMessage(
            $"operator-failure:{correlationId}",
            OutboundEmailMessageType.OperatorFailure,
            [configuration.OperatorEmail],
            "Failure to process submission or send email",
            $"Correlation ID: {correlationId}{Environment.NewLine}{Environment.NewLine}{failureSummary}{Environment.NewLine}{Environment.NewLine}{rawSubmissionJson}");
    }

    public static IReadOnlyList<string> BuildInterestedPartyRecipients(NormalizedInterestFormSubmission submission)
    {
        var recipients = new List<string>();
        var submitterType = SubmissionNormalizer.GetSubmitterType(submission.SubmissionType);

        AddEmail(recipients, submission.StudentEmail);
        AddEmail(recipients, submission.ParentEmail);

        if (submitterType == InterestFormSubmitterType.Student)
        {
            return recipients
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        AddEmail(recipients, submission.ContactEmail);
        return recipients
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private OutboundEmailMessage BuildDistrictForwardingMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        var recipients = BuildRecipients(
            route.DistrictContacts.SelectMany(contact => contact.EmailAddresses),
            BuildInterestedPartyRecipients(submission),
            ShouldCopySupport(submission) ? [configuration.SupportEmail] : []);

        return new OutboundEmailMessage(
            $"district:{submission.Id}",
            OutboundEmailMessageType.DistrictRepresentative,
            recipients,
            BuildForwardingSubject(submission),
            BuildLocalizedBodies(
                submission,
                language => BuildSharedBody(
                    BuildSubmitterGreeting(submission, language),
                    BuildDistrictIntro(submission, route, language),
                    submission,
                    Localized(language, "your district", "votre district", "su distrito"),
                    BuildRecipientSectionLabel(submission, isManualRouting: false, language),
                    language: language)),
            true);
    }

    private OutboundEmailMessage BuildCountryForwardingMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        var recipients = BuildRecipients(
            route.CountryContact?.EmailAddresses ?? [],
            BuildInterestedPartyRecipients(submission),
            ShouldCopySupport(submission) ? [configuration.SupportEmail] : []);

        var country = route.CountryContact?.Country ?? submission.CountryOfResidence;

        return new OutboundEmailMessage(
            $"country:{submission.Id}",
            OutboundEmailMessageType.CountryRepresentative,
            recipients,
            BuildForwardingSubject(submission),
            BuildLocalizedBodies(
                submission,
                language => BuildSharedBody(
                    BuildSubmitterGreeting(submission, language),
                    BuildCountryIntro(submission, country, language),
                    submission,
                    Localized(language, "your country", "votre pays", "su país"),
                    BuildRecipientSectionLabel(submission, isManualRouting: false, language),
                    language: language)),
            true);
    }

    private OutboundEmailMessage BuildManualRoutingMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        var recipients = BuildRecipients(
            [configuration.OperatorEmail],
            BuildInterestedPartyRecipients(submission),
            ShouldCopySupport(submission) ? [configuration.SupportEmail] : []);

        return new OutboundEmailMessage(
            $"operator-fallback:{submission.Id}",
            OutboundEmailMessageType.OperatorFallback,
            recipients,
            BuildManualRoutingSubject(submission),
            BuildLocalizedBodies(
                submission,
                language => BuildSharedBody(
                    BuildSubmitterGreeting(submission, language),
                    BuildManualRoutingIntro(submission, language),
                    submission,
                    Localized(language, "this submission", "cette demande", "este formulario"),
                    BuildRecipientSectionLabel(submission, isManualRouting: true, language),
                    route.Errors,
                    language)),
            true);
    }

    private bool ShouldCopySupport(NormalizedInterestFormSubmission submission)
    {
        var submitterType = SubmissionNormalizer.GetSubmitterType(submission.SubmissionType);
        return submitterType is InterestFormSubmitterType.Rotarian or InterestFormSubmitterType.Other;
    }

    private static IReadOnlyList<string> BuildRecipients(params IEnumerable<string?>[] recipientGroups)
    {
        var recipients = new List<string>();
        foreach (var group in recipientGroups)
        {
            foreach (var email in group)
            {
                AddEmail(recipients, email);
            }
        }

        return recipients
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddEmail(List<string> recipients, string? email)
    {
        if (EmailAddressUtility.IsUsable(email))
        {
            recipients.Add(email!.Trim());
        }
    }

    private static string BuildSubmitterGreeting(
        NormalizedInterestFormSubmission submission,
        EmailLanguage language)
    {
        if (IsRotarianSubmission(submission))
        {
            return Localized(language, "Hello fellow Rotarian,", "Bonjour, cher membre du Rotary,", "Hola, colega rotario:");
        }

        return string.IsNullOrWhiteSpace(submission.Name)
            ? Localized(language, "Hello,", "Bonjour,", "Hola:")
            : Localized(
                language,
                $"Hello {Html(submission.Name.Trim())},",
                $"Bonjour {Html(submission.Name.Trim())},",
                $"Hola, {Html(submission.Name.Trim())}:");
    }

    private static string BuildForwardingSubject(NormalizedInterestFormSubmission submission)
    {
        return string.Join(
            " / ",
            GetEmailLanguages(submission).Select(language => language switch
            {
                EmailLanguage.French => IsRotarianSubmission(submission)
                    ? $"Question sur les échanges de jeunes du Rotary de {UnknownIfBlank(submission.Name)}"
                    : $"Intérêt pour les échanges de jeunes du Rotary de {UnknownIfBlank(submission.Name)}",
                EmailLanguage.Spanish => IsRotarianSubmission(submission)
                    ? $"Pregunta sobre el Intercambio de Jóvenes de Rotary de {UnknownIfBlank(submission.Name)}"
                    : $"Interés en el Intercambio de Jóvenes de Rotary de {UnknownIfBlank(submission.Name)}",
                _ => $"Rotary Youth Exchange {(IsRotarianSubmission(submission) ? "question" : "interest")} from {UnknownIfBlank(submission.Name)}"
            }));
    }

    private static string BuildManualRoutingSubject(NormalizedInterestFormSubmission submission)
    {
        return string.Join(
            " / ",
            GetEmailLanguages(submission).Select(language => Localized(
                language,
                "Rotary Youth Exchange interest needs routing review",
                "La demande d'intérêt pour les échanges de jeunes du Rotary doit être examinée",
                "El interés en el Intercambio de Jóvenes de Rotary requiere revisión de asignación")));
    }

    private static IReadOnlyList<string> BuildDistrictIntro(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route,
        EmailLanguage language)
    {
        if (!route.HasMultipleDistrictMatches)
        {
            var districtName = route.DistrictContacts.Count == 0
                ? Localized(language, "your local district", "votre district local", "su distrito local")
                : FormatDistrictForGreeting(route.DistrictContacts[0].District, language);

            if (IsRotarianSubmission(submission))
            {
                return
                [
                    Localized(language, "Thank you for participating in Rotary Youth Exchange and for reaching out with your question about the Study Abroad Scholarships offered as part of the program.", "Merci de participer aux échanges de jeunes du Rotary et de nous avoir envoyé votre question au sujet des bourses d'études à l'étranger offertes dans le cadre du programme.", "Gracias por participar en el Intercambio de Jóvenes de Rotary y por comunicarse con su pregunta sobre las Becas para Estudios en el Extranjero que se ofrecen como parte del programa."),
                    Localized(language, $"The Rotary Youth Exchange representatives from {Html(districtName)} and our support team have been added to this email.", $"Les représentants des échanges de jeunes du Rotary du {Html(districtName)} et notre équipe de soutien ont été ajoutés à ce courriel.", $"Los representantes del Intercambio de Jóvenes de Rotary del {Html(districtName)} y nuestro equipo de apoyo han sido incluidos en este correo electrónico."),
                    Localized(language, "To keep every representative and the support team included, choose <strong>&ldquo;Reply all&rdquo;</strong> when sending additional details or questions.", "Afin que tous les représentants et l'équipe de soutien restent inclus, choisissez <strong>&laquo;&nbsp;Répondre à tous&nbsp;&raquo;</strong> lorsque vous envoyez des renseignements supplémentaires ou des questions.", "Para mantener incluidos a todos los representantes y al equipo de apoyo, elija <strong>&laquo;Responder a todos&raquo;</strong> cuando envíe información adicional o preguntas."),
                    Localized(language, "They should reply within 2 weeks with guidance specific to your area.", "Ils devraient vous répondre dans un délai de deux semaines avec des conseils propres à votre région.", "Deberían responder en un plazo de 2 semanas con orientación específica para su zona.")
                ];
            }

            return
            [
                Localized(language, "Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.", "Merci de nous avoir contactés pour en savoir plus sur les bourses d'études à l'étranger offertes dans le cadre des échanges de jeunes du Rotary.", "Gracias por comunicarse para obtener más información sobre las Becas para Estudios en el Extranjero que se ofrecen como parte del Intercambio de Jóvenes de Rotary."),
                Localized(language, $"Your local Rotary Youth Exchange representatives from {Html(districtName)} have been added to this email.", $"Vos représentants locaux des échanges de jeunes du Rotary du {Html(districtName)} ont été ajoutés à ce courriel.", $"Sus representantes locales del Intercambio de Jóvenes de Rotary del {Html(districtName)} han sido incluidos en este correo electrónico."),
                Localized(language, "To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions.", "Pour que tout le monde voie votre message, choisissez <strong>&laquo;&nbsp;Répondre à tous&nbsp;&raquo;</strong> lorsque vous posez vos questions.", "Para asegurarse de que todos vean su mensaje, elija <strong>&laquo;Responder a todos&raquo;</strong> cuando haga sus preguntas."),
                Localized(language, "They should reply within 2 weeks with information about how the program works in your area.", "Ils devraient vous répondre dans un délai de deux semaines avec des renseignements sur le fonctionnement du programme dans votre région.", "Deberían responder en un plazo de 2 semanas con información sobre cómo funciona el programa en su zona.")
            ];
        }

        var districtNames = JoinForSentence(
            route.DistrictContacts.Select(contact => FormatDistrictForGreeting(contact.District, language)),
            language);
        if (IsRotarianSubmission(submission))
        {
            return
            [
                Localized(language, "Thank you for participating in Rotary Youth Exchange and for reaching out with your question about the Study Abroad Scholarships offered as part of the program.", "Merci de participer aux échanges de jeunes du Rotary et de nous avoir envoyé votre question au sujet des bourses d'études à l'étranger offertes dans le cadre du programme.", "Gracias por participar en el Intercambio de Jóvenes de Rotary y por comunicarse con su pregunta sobre las Becas para Estudios en el Extranjero que se ofrecen como parte del programa."),
                Localized(language, $"Your location matched multiple Rotary districts ({Html(districtNames)}), so representatives from each district and our support team have been added to this email.", $"Votre emplacement correspond à plusieurs districts du Rotary ({Html(districtNames)}); les représentants de chaque district et notre équipe de soutien ont donc été ajoutés à ce courriel.", $"Su ubicación coincide con varios distritos de Rotary ({Html(districtNames)}), por lo que los representantes de cada distrito y nuestro equipo de apoyo han sido incluidos en este correo electrónico."),
                Localized(language, "To keep every representative and the support team included, choose <strong>&ldquo;Reply all&rdquo;</strong> when sending additional details or questions.", "Afin que tous les représentants et l'équipe de soutien restent inclus, choisissez <strong>&laquo;&nbsp;Répondre à tous&nbsp;&raquo;</strong> lorsque vous envoyez des renseignements supplémentaires ou des questions.", "Para mantener incluidos a todos los representantes y al equipo de apoyo, elija <strong>&laquo;Responder a todos&raquo;</strong> cuando envíe información adicional o preguntas."),
                Localized(language, "They should reply within 2 weeks with guidance specific to your area.", "Ils devraient vous répondre dans un délai de deux semaines avec des conseils propres à votre région.", "Deberían responder en un plazo de 2 semanas con orientación específica para su zona.")
            ];
        }

        return
        [
            Localized(language, "Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.", "Merci de nous avoir contactés pour en savoir plus sur les bourses d'études à l'étranger offertes dans le cadre des échanges de jeunes du Rotary.", "Gracias por comunicarse para obtener más información sobre las Becas para Estudios en el Extranjero que se ofrecen como parte del Intercambio de Jóvenes de Rotary."),
            Localized(language, $"Your location matched multiple Rotary districts ({Html(districtNames)}), so representatives from each district have been added to this email.", $"Votre emplacement correspond à plusieurs districts du Rotary ({Html(districtNames)}); les représentants de chaque district ont donc été ajoutés à ce courriel.", $"Su ubicación coincide con varios distritos de Rotary ({Html(districtNames)}), por lo que los representantes de cada distrito han sido incluidos en este correo electrónico."),
            Localized(language, "To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions.", "Pour que tout le monde voie votre message, choisissez <strong>&laquo;&nbsp;Répondre à tous&nbsp;&raquo;</strong> lorsque vous posez vos questions.", "Para asegurarse de que todos vean su mensaje, elija <strong>&laquo;Responder a todos&raquo;</strong> cuando haga sus preguntas."),
            Localized(language, "They should reply within 2 weeks with information about how the program works in your area.", "Ils devraient vous répondre dans un délai de deux semaines avec des renseignements sur le fonctionnement du programme dans votre région.", "Deberían responder en un plazo de 2 semanas con información sobre cómo funciona el programa en su zona.")
        ];
    }

    private static IReadOnlyList<string> BuildCountryIntro(
        NormalizedInterestFormSubmission submission,
        string? country,
        EmailLanguage language)
    {
        var displayCountry = Html(GetDisplayCountryName(country, language));
        if (IsRotarianSubmission(submission))
        {
            return
            [
                Localized(language, "Thank you for participating in Rotary Youth Exchange and for reaching out with your question about the Study Abroad Scholarships offered as part of the program.", "Merci de participer aux échanges de jeunes du Rotary et de nous avoir envoyé votre question au sujet des bourses d'études à l'étranger offertes dans le cadre du programme.", "Gracias por participar en el Intercambio de Jóvenes de Rotary y por comunicarse con su pregunta sobre las Becas para Estudios en el Extranjero que se ofrecen como parte del programa."),
                Localized(language, $"The Rotary Youth Exchange representatives for {displayCountry} and our support team have been added to this email.", $"Les représentants des échanges de jeunes du Rotary pour le {displayCountry} et notre équipe de soutien ont été ajoutés à ce courriel.", $"Los representantes del Intercambio de Jóvenes de Rotary de {displayCountry} y nuestro equipo de apoyo han sido incluidos en este correo electrónico."),
                Localized(language, "To keep every representative and the support team included, choose <strong>&ldquo;Reply all&rdquo;</strong> when sending additional details or questions.", "Afin que tous les représentants et l'équipe de soutien restent inclus, choisissez <strong>&laquo;&nbsp;Répondre à tous&nbsp;&raquo;</strong> lorsque vous envoyez des renseignements supplémentaires ou des questions.", "Para mantener incluidos a todos los representantes y al equipo de apoyo, elija <strong>&laquo;Responder a todos&raquo;</strong> cuando envíe información adicional o preguntas."),
                Localized(language, "They should reply within 2 weeks with guidance specific to your area.", "Ils devraient vous répondre dans un délai de deux semaines avec des conseils propres à votre région.", "Deberían responder en un plazo de 2 semanas con orientación específica para su zona.")
            ];
        }

        return
        [
            Localized(language, "Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.", "Merci de nous avoir contactés pour en savoir plus sur les bourses d'études à l'étranger offertes dans le cadre des échanges de jeunes du Rotary.", "Gracias por comunicarse para obtener más información sobre las Becas para Estudios en el Extranjero que se ofrecen como parte del Intercambio de Jóvenes de Rotary."),
            Localized(language, $"The Rotary Youth Exchange representatives for {displayCountry} have been added to this email.", $"Les représentants des échanges de jeunes du Rotary pour le {displayCountry} ont été ajoutés à ce courriel.", $"Los representantes del Intercambio de Jóvenes de Rotary de {displayCountry} han sido incluidos en este correo electrónico."),
            Localized(language, "To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions.", "Pour que tout le monde voie votre message, choisissez <strong>&laquo;&nbsp;Répondre à tous&nbsp;&raquo;</strong> lorsque vous posez vos questions.", "Para asegurarse de que todos vean su mensaje, elija <strong>&laquo;Responder a todos&raquo;</strong> cuando haga sus preguntas."),
            Localized(language, "They should reply within 2 weeks with information about how the program works in your area.", "Ils devraient vous répondre dans un délai de deux semaines avec des renseignements sur le fonctionnement du programme dans votre région.", "Deberían responder en un plazo de 2 semanas con información sobre cómo funciona el programa en su zona.")
        ];
    }

    private static IReadOnlyList<string> BuildManualRoutingIntro(
        NormalizedInterestFormSubmission submission,
        EmailLanguage language)
    {
        if (IsRotarianSubmission(submission))
        {
            return
            [
                Localized(language, "Thank you for participating in Rotary Youth Exchange and for reaching out with your question about the Study Abroad Scholarships offered as part of the program.", "Merci de participer aux échanges de jeunes du Rotary et de nous avoir envoyé votre question au sujet des bourses d'études à l'étranger offertes dans le cadre du programme.", "Gracias por participar en el Intercambio de Jóvenes de Rotary y por comunicarse con su pregunta sobre las Becas para Estudios en el Extranjero que se ofrecen como parte del programa."),
                Localized(language, "We could not automatically identify the Rotary Youth Exchange representatives for your area, so our admin and support teams have been added to this email to review your request.", "Nous n'avons pas pu identifier automatiquement les représentants des échanges de jeunes du Rotary de votre région. Nos équipes administrative et de soutien ont donc été ajoutées à ce courriel pour examiner votre demande.", "No pudimos identificar automáticamente a los representantes del Intercambio de Jóvenes de Rotary de su zona, por lo que nuestros equipos administrativo y de apoyo han sido incluidos en este correo electrónico para revisar su solicitud."),
                Localized(language, "They should reply within 2 weeks with guidance about the next steps.", "Ils devraient vous répondre dans un délai de deux semaines avec des conseils sur les prochaines étapes.", "Deberían responder en un plazo de 2 semanas con orientación sobre los próximos pasos.")
            ];
        }

        return
        [
            Localized(language, "Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.", "Merci de nous avoir contactés pour en savoir plus sur les bourses d'études à l'étranger offertes dans le cadre des échanges de jeunes du Rotary.", "Gracias por comunicarse para obtener más información sobre las Becas para Estudios en el Extranjero que se ofrecen como parte del Intercambio de Jóvenes de Rotary."),
            Localized(language, "We could not automatically identify the Rotary Youth Exchange representatives for your area, so our admin team has been added to this email to review your request.", "Nous n'avons pas pu identifier automatiquement les représentants des échanges de jeunes du Rotary de votre région. Notre équipe administrative a donc été ajoutée à ce courriel pour examiner votre demande.", "No pudimos identificar automáticamente a los representantes del Intercambio de Jóvenes de Rotary de su zona, por lo que nuestro equipo administrativo ha sido incluido en este correo electrónico para revisar su solicitud."),
            Localized(language, "The admin team should reply within 2 weeks with information about the next steps.", "L'équipe administrative devrait vous répondre dans un délai de deux semaines avec des renseignements sur les prochaines étapes.", "El equipo administrativo debería responder en un plazo de 2 semanas con información sobre los próximos pasos.")
        ];
    }

    private static bool IsRotarianSubmission(NormalizedInterestFormSubmission submission)
    {
        return SubmissionNormalizer.GetSubmitterType(submission.SubmissionType) == InterestFormSubmitterType.Rotarian;
    }

    private static string BuildSubmitterSectionLabel(
        NormalizedInterestFormSubmission submission,
        EmailLanguage language)
    {
        return SubmissionNormalizer.GetSubmitterType(submission.SubmissionType) switch
        {
            InterestFormSubmitterType.Student => Localized(language, "For the submitting student:", "Pour l'élève qui a soumis la demande :", "Para el estudiante que envió el formulario:"),
            InterestFormSubmitterType.Parent => Localized(language, "For the submitting family:", "Pour la famille qui a soumis la demande :", "Para la familia que envió el formulario:"),
            InterestFormSubmitterType.Rotarian => Localized(language, "For the submitting Rotarian:", "Pour le membre du Rotary qui a soumis la demande :", "Para el rotario que envió el formulario:"),
            _ => Localized(language, "For the submitter:", "Pour la personne qui a soumis la demande :", "Para la persona que envió el formulario:")
        };
    }

    private static string BuildRecipientSectionLabel(
        NormalizedInterestFormSubmission submission,
        bool isManualRouting,
        EmailLanguage language)
    {
        if (isManualRouting)
        {
            return IsRotarianSubmission(submission)
                ? Localized(language, "For the Rotary admin and support teams:", "Pour les équipes administrative et de soutien du Rotary :", "Para los equipos administrativo y de apoyo de Rotary:")
                : Localized(language, "For the Rotary admin team:", "Pour l'équipe administrative du Rotary :", "Para el equipo administrativo de Rotary:");
        }

        return IsRotarianSubmission(submission)
            ? Localized(language, "For the Rotary representatives and support team:", "Pour les représentants du Rotary et l'équipe de soutien :", "Para los representantes de Rotary y el equipo de apoyo:")
            : Localized(language, "For the Rotary representative:", "Pour le représentant du Rotary :", "Para el representante de Rotary:");
    }

    private static string FormatDistrictForGreeting(string? district, EmailLanguage language)
    {
        var trimmed = UnknownIfBlank(district);
        return trimmed.StartsWith("district ", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{Localized(language, "District", "district", "Distrito")} {trimmed}";
    }

    private string BuildSharedBody(
        string greeting,
        IReadOnlyList<string> introParagraphs,
        NormalizedInterestFormSubmission submission,
        string supportContext,
        string recipientSectionLabel,
        IReadOnlyList<string>? routingErrors = null,
        EmailLanguage language = EmailLanguage.English)
    {
        var sections = new List<string>
        {
            Paragraph(greeting),
            SectionLabel(BuildSubmitterSectionLabel(submission, language))
        };

        sections.AddRange(introParagraphs.Select(Paragraph));
        sections.AddRange(
        [
            Paragraph(Localized(language, "For reference, here is the information you submitted:", "À titre de référence, voici les renseignements que vous avez soumis :", "Como referencia, esta es la información que envió:")),
            SubmissionInformationBlock(submission, language)
        ]);

        if (routingErrors?.Count > 0)
        {
            sections.Add(Paragraph($"{Localized(language, "Routing notes", "Notes d'acheminement", "Notas de asignación")}: {Html(string.Join("; ", routingErrors))}"));
        }

        sections.Add(SectionLabel(recipientSectionLabel));
        var representativeIntro = IsRotarianSubmission(submission)
            ? Localized(language, "This question was submitted by a fellow Rotarian. ", "Cette question a été soumise par un autre membre du Rotary. ", "Esta pregunta fue enviada por otro rotario. ")
            : string.Empty;
        sections.Add(Paragraph(
            representativeIntro + Localized(
                language,
                $"If you have any admin support questions, need advice about the process, need to add or remove email addresses for {Html(supportContext)}, or want a list of previous submissions, please contact {SupportEmailLink()}.",
                $"Si vous avez des questions de soutien administratif, avez besoin de conseils sur le processus, devez ajouter ou supprimer des adresses courriel pour {Html(supportContext)}, ou souhaitez obtenir une liste des demandes précédentes, veuillez communiquer avec {SupportEmailLink()}.",
                $"Si tiene preguntas de apoyo administrativo, necesita asesoramiento sobre el proceso, necesita agregar o eliminar direcciones de correo electrónico para {Html(supportContext)}, o desea una lista de formularios anteriores, comuníquese con {SupportEmailLink()}.")));
        var closing = IsRotarianSubmission(submission)
            ? Localized(language, $"Thank you for participating in Rotary Youth Exchange and supporting the Study Abroad Scholarships through {SiteLink()}!", $"Merci de participer aux échanges de jeunes du Rotary et de soutenir les bourses d'études à l'étranger par l'intermédiaire de {SiteLink()}!", $"¡Gracias por participar en el Intercambio de Jóvenes de Rotary y apoyar las Becas para Estudios en el Extranjero a través de {SiteLink()}!")
            : Localized(language, $"Thank you for your interest in the Study Abroad Scholarships offered through Rotary Youth Exchange at {SiteLink()}!", $"Merci de votre intérêt pour les bourses d'études à l'étranger offertes dans le cadre des échanges de jeunes du Rotary à {SiteLink()}!", $"¡Gracias por su interés en las Becas para Estudios en el Extranjero que ofrece el Intercambio de Jóvenes de Rotary en {SiteLink()}!");
        sections.Add(Paragraph(closing));

        return string.Join(Environment.NewLine, sections);
    }

    private static string SubmissionInformationBlock(
        NormalizedInterestFormSubmission submission,
        EmailLanguage language)
    {
        var lines = new List<string>();
        AddLine(lines, Localized(language, "Who are you?", "Qui êtes-vous?", "¿Quién es usted?"), LocalizedSubmissionType(submission.SubmissionType, language));
        AddLine(lines, Localized(language, "Name", "Nom", "Nombre"), submission.Name);
        AddLine(lines, Localized(language, "Current age (years)", "Âge actuel (années)", "Edad actual (años)"), submission.Age);
        AddLine(lines, Localized(language, "Current age of your student (years)", "Âge actuel de votre enfant (années)", "Edad actual de su estudiante (años)"), submission.ParentEnteredAge);
        AddLine(lines, Localized(language, "Student's email", "Courriel de l'élève", "Correo electrónico del estudiante"), submission.StudentEmail);
        AddLine(lines, Localized(language, "Student's phone number", "Numéro de téléphone de l'élève", "Teléfono del estudiante"), submission.StudentPhone);
        AddLine(lines, Localized(language, "Parent's email", "Courriel du parent", "Correo electrónico del padre, madre o tutor"), submission.ParentEmail);
        AddLine(lines, Localized(language, "Parent's phone number", "Numéro de téléphone du parent", "Teléfono del padre, madre o tutor"), submission.ParentPhone);
        AddLine(lines, Localized(language, "Contact email", "Courriel de la personne-ressource", "Correo electrónico de contacto"), submission.ContactEmail);
        AddLine(lines, Localized(language, "Contact phone number", "Numéro de téléphone de la personne-ressource", "Teléfono de contacto"), submission.ContactPhone);
        AddCountryLine(lines, submission.CountryOfResidence, language);
        AddLine(lines, Localized(language, "State or province", "État ou province", "Estado o provincia"), submission.State);
        AddLine(lines, Localized(language, "City", "Ville", "Ciudad"), submission.City);
        AddLine(lines, Localized(language, "Zip code or first 3 of CDN postal code", "Code postal ou trois premiers caractères du code postal canadien", "Código postal o los primeros 3 caracteres del código postal canadiense"), submission.Zipcode);
        AddLine(lines, Localized(language, "Question", "Question", "Pregunta"), submission.OptionalSubmissionQuestion);

        return lines.Count == 0
            ? Paragraph(Localized(language, "No form details were provided.", "Aucun renseignement n'a été fourni dans le formulaire.", "No se proporcionaron datos en el formulario."))
            : $"<p>{string.Join("<br>", lines)}</p>";
    }

    private static string GetDisplayCountryName(string? country, EmailLanguage language = EmailLanguage.English)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return Localized(language, "Unknown", "Inconnu", "Desconocido");
        }

        var normalized = SubmissionNormalizer.NormalizeCountry(country);
        return normalized switch
        {
            "usa" => Localized(language, "USA", "États-Unis", "EE. UU."),
            "canada" => "Canada",
            "mexico" => Localized(language, "Mexico", "Mexique", "México"),
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(country.Trim().ToLowerInvariant())
        };
    }

    private static string UnknownIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }

    private static void AddLine(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"<strong>{label}:</strong> {Html(value.Trim())}");
        }
    }

    private static void AddCountryLine(List<string> lines, string? country, EmailLanguage language)
    {
        if (!string.IsNullOrWhiteSpace(country))
        {
            lines.Add($"<strong>{Localized(language, "Country of residence", "Pays de résidence", "País de residencia")}:</strong> {Html(GetDisplayCountryName(country, language))}");
        }
    }

    private static string Paragraph(string text)
    {
        return $"<p>{text}</p>";
    }

    private static string BuildLocalizedBodies(
        NormalizedInterestFormSubmission submission,
        Func<EmailLanguage, string> buildBody)
    {
        return string.Join(
            $"{Environment.NewLine}<hr>{Environment.NewLine}",
            GetEmailLanguages(submission).Select(buildBody));
    }

    private static IReadOnlyList<EmailLanguage> GetEmailLanguages(
        NormalizedInterestFormSubmission submission)
    {
        return SubmissionNormalizer.NormalizeCountry(submission.CountryOfResidence) switch
        {
            "canada" => [EmailLanguage.English, EmailLanguage.French],
            "mexico" => [EmailLanguage.Spanish],
            _ => [EmailLanguage.English]
        };
    }

    private static string Localized(
        EmailLanguage language,
        string english,
        string french,
        string spanish)
    {
        return language switch
        {
            EmailLanguage.French => french,
            EmailLanguage.Spanish => spanish,
            _ => english
        };
    }

    private static string? LocalizedSubmissionType(string? submissionType, EmailLanguage language)
    {
        if (string.IsNullOrWhiteSpace(submissionType) || language == EmailLanguage.English)
        {
            return submissionType;
        }

        return SubmissionNormalizer.GetSubmitterType(submissionType) switch
        {
            InterestFormSubmitterType.Student => Localized(language, "Student", "Élève", "Estudiante"),
            InterestFormSubmitterType.Parent => Localized(language, "Parent", "Parent", "Padre, madre o tutor"),
            InterestFormSubmitterType.Rotarian => Localized(language, "Rotarian", "Membre du Rotary", "Rotario"),
            _ => Localized(language, submissionType, "Autre", "Otra persona")
        };
    }

    private static string SectionLabel(string text)
    {
        return $"<p><strong><u>{Html(text)}</u></strong></p>";
    }

    private static string SiteLink()
    {
        return $"""<a href="{PublicSiteUrl}">{PublicSiteDisplayName}</a>""";
    }

    private string SupportEmailLink()
    {
        var operatorEmail = Html(configuration.OperatorEmail);
        return $"""<a href="mailto:{operatorEmail}">{operatorEmail}</a>""";
    }

    private static string JoinForSentence(IEnumerable<string> values, EmailLanguage language)
    {
        var items = values.ToList();
        var conjunction = Localized(language, "and", "et", "y");
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            2 => $"{items[0]} {conjunction} {items[1]}",
            _ => $"{string.Join(", ", items.Take(items.Count - 1))}, {conjunction} {items[^1]}"
        };
    }

    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value);
    }
}
