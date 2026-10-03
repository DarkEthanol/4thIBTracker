# Google OAuth verification checklist

Use these exact public URLs in Google Auth Platform:

- Application name: `4thIB Tracker`
- Homepage: `https://tracker.ethanolgaming.co.uk/`
- Privacy policy: `https://tracker.ethanolgaming.co.uk/privacy/`
- Terms of service: `https://tracker.ethanolgaming.co.uk/terms/`
- User data management: `https://tracker.ethanolgaming.co.uk/data/`
- Authorised domain: `ethanolgaming.co.uk`

## Requested scope

`https://www.googleapis.com/auth/spreadsheets`

## Scope justification

4thIB Tracker is a Windows desktop productivity application for authorised
community administrators. It reads cell ranges from existing, user-configured
spreadsheets to display attendance, training, qualifications, logistics and
personnel records. It writes user-reviewed attendance and administrative changes
back to those configured spreadsheets only through visible app features and an
explicit save action. Read/write Sheets access is required because a read-only
scope cannot support those edits. The application does not request Google Drive,
Gmail or Contacts access and does not copy Google user data to a developer-owned
server.

## Before submission

1. Add the site hostname to DNS and confirm every public URL uses valid HTTPS.
2. Verify the `ethanolgaming.co.uk` Domain Property in Google Search Console
   using the same Google account that is a project Owner or Editor.
3. In Google Auth Platform → Branding, use the URLs above and ensure the support
   and developer-contact email addresses are current.
4. In Audience, select External and In production.
5. In Data Access, declare only the Sheets scope shown above.
6. Record an unlisted demonstration video in English showing:
   - the 4thIB Tracker name and this homepage;
   - Settings → Google Access → Connect Google;
   - the complete OAuth consent screen and requested scope;
   - a Google-backed read-only view loading data;
   - an editable attendance change followed by Save to Sheet; and
   - Settings → Google Access → Sign out.
7. Submit the scope justification and unlisted video through Prepare for
   verification.

Do not include real personnel data in the demonstration. Use a dedicated test
spreadsheet populated with fictional records.
