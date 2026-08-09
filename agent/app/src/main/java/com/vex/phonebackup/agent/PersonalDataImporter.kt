package com.vex.phonebackup.agent

import android.Manifest
import android.content.ContentProviderOperation
import android.content.ContentValues
import android.content.Context
import android.content.pm.PackageManager
import android.provider.CallLog
import android.provider.ContactsContract
import android.telephony.PhoneNumberUtils
import androidx.core.content.ContextCompat
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream

object PersonalDataImporter {
    fun restore(
        context: Context,
        kind: String,
        consume: (OutputStream) -> Unit
    ): JSONObject {
        if (kind !in setOf("contacts", "calls", "messages"))
            return JSONObject().put("ok", false).put("error", "invalid_personal_kind")
        val temporary = File(context.noBackupFilesDir,
            "personal-$kind-${System.nanoTime()}.tmp")
        return try {
            FileOutputStream(temporary).buffered(256 * 1024).use(consume)
            when (kind) {
                "contacts" -> restoreContacts(context, temporary)
                "calls" -> restoreCalls(context, temporary)
                else -> JSONObject()
                    .put("ok", true).put("status", "needs_user")
                    .put("reason", "temporary_default_sms_role_not_available")
                    .put("inserted", 0).put("skipped", 0).put("failed", 0)
            }
        } catch (error: Exception) {
            JSONObject().put("ok", false).put("error", error.message ?: "personal_restore_failed")
        } finally {
            temporary.delete()
        }
    }

    private fun restoreContacts(context: Context, source: File): JSONObject {
        require(granted(context, Manifest.permission.WRITE_CONTACTS)) {
            "write_contacts_permission_required"
        }
        val existingPhones = mutableSetOf<String>()
        context.contentResolver.query(
            ContactsContract.CommonDataKinds.Phone.CONTENT_URI,
            arrayOf(ContactsContract.CommonDataKinds.Phone.NUMBER), null, null, null
        )?.use { cursor ->
            while (cursor.moveToNext()) normalizePhone(cursor.getString(0))
                .takeIf(String::isNotBlank)?.let(existingPhones::add)
        }
        val existingEmails = mutableSetOf<String>()
        context.contentResolver.query(
            ContactsContract.CommonDataKinds.Email.CONTENT_URI,
            arrayOf(ContactsContract.CommonDataKinds.Email.ADDRESS), null, null, null
        )?.use { cursor ->
            while (cursor.moveToNext()) cursor.getString(0)?.trim()?.lowercase()
                ?.takeIf(String::isNotBlank)?.let(existingEmails::add)
        }

        var inserted = 0
        var skipped = 0
        var failed = 0
        parseVCards(source.readText()).forEach { card ->
            val phones = card.phones.map(::normalizePhone).filter(String::isNotBlank).distinct()
            val emails = card.emails.map { it.trim().lowercase() }
                .filter(String::isNotBlank).distinct()
            if (phones.any(existingPhones::contains) || emails.any(existingEmails::contains)) {
                skipped++
                return@forEach
            }
            val operations = arrayListOf(
                ContentProviderOperation.newInsert(ContactsContract.RawContacts.CONTENT_URI)
                    .withValue(ContactsContract.RawContacts.ACCOUNT_TYPE, null)
                    .withValue(ContactsContract.RawContacts.ACCOUNT_NAME, null)
                    .build()
            )
            if (card.name.isNotBlank()) operations += ContentProviderOperation
                .newInsert(ContactsContract.Data.CONTENT_URI)
                .withValueBackReference(ContactsContract.Data.RAW_CONTACT_ID, 0)
                .withValue(ContactsContract.Data.MIMETYPE,
                    ContactsContract.CommonDataKinds.StructuredName.CONTENT_ITEM_TYPE)
                .withValue(ContactsContract.CommonDataKinds.StructuredName.DISPLAY_NAME, card.name)
                .build()
            phones.forEach { phone -> operations += ContentProviderOperation
                .newInsert(ContactsContract.Data.CONTENT_URI)
                .withValueBackReference(ContactsContract.Data.RAW_CONTACT_ID, 0)
                .withValue(ContactsContract.Data.MIMETYPE,
                    ContactsContract.CommonDataKinds.Phone.CONTENT_ITEM_TYPE)
                .withValue(ContactsContract.CommonDataKinds.Phone.NUMBER, phone)
                .withValue(ContactsContract.CommonDataKinds.Phone.TYPE,
                    ContactsContract.CommonDataKinds.Phone.TYPE_OTHER)
                .build() }
            emails.forEach { email -> operations += ContentProviderOperation
                .newInsert(ContactsContract.Data.CONTENT_URI)
                .withValueBackReference(ContactsContract.Data.RAW_CONTACT_ID, 0)
                .withValue(ContactsContract.Data.MIMETYPE,
                    ContactsContract.CommonDataKinds.Email.CONTENT_ITEM_TYPE)
                .withValue(ContactsContract.CommonDataKinds.Email.ADDRESS, email)
                .withValue(ContactsContract.CommonDataKinds.Email.TYPE,
                    ContactsContract.CommonDataKinds.Email.TYPE_OTHER)
                .build() }
            if (operations.size == 1) {
                skipped++
            } else if (runCatching {
                    context.contentResolver.applyBatch(ContactsContract.AUTHORITY, operations)
                }.isSuccess) {
                inserted++
                existingPhones += phones
                existingEmails += emails
            } else failed++
        }
        return result(inserted, skipped, failed)
    }

    private fun restoreCalls(context: Context, source: File): JSONObject {
        require(granted(context, Manifest.permission.WRITE_CALL_LOG)) {
            "write_call_log_permission_required"
        }
        val existing = mutableSetOf<String>()
        context.contentResolver.query(
            CallLog.Calls.CONTENT_URI,
            arrayOf(CallLog.Calls.NUMBER, CallLog.Calls.DATE, CallLog.Calls.DURATION,
                CallLog.Calls.TYPE), null, null, null
        )?.use { cursor ->
            while (cursor.moveToNext()) existing += callKey(
                cursor.getString(0), cursor.getLong(1), cursor.getLong(2), cursor.getInt(3))
        }
        var inserted = 0
        var skipped = 0
        var failed = 0
        source.useLines { lines -> lines.forEach { line ->
            if (line.isBlank() || line.length > 1024 * 1024) return@forEach
            val item = runCatching { JSONObject(line) }.getOrNull()
            if (item == null) { failed++; return@forEach }
            val number = item.optString(CallLog.Calls.NUMBER)
            val date = item.optLong(CallLog.Calls.DATE, -1)
            val duration = item.optLong(CallLog.Calls.DURATION, -1)
            val type = item.optInt(CallLog.Calls.TYPE, -1)
            if (date < 0 || duration < 0 || type !in 1..10) { failed++; return@forEach }
            val key = callKey(number, date, duration, type)
            if (!existing.add(key)) { skipped++; return@forEach }
            val values = ContentValues().apply {
                put(CallLog.Calls.NUMBER, number.take(512))
                put(CallLog.Calls.CACHED_NAME, item.optString(CallLog.Calls.CACHED_NAME).take(512))
                put(CallLog.Calls.DATE, date)
                put(CallLog.Calls.DURATION, duration)
                put(CallLog.Calls.TYPE, type)
                put(CallLog.Calls.NEW, item.optInt(CallLog.Calls.NEW, 0).coerceIn(0, 1))
            }
            if (runCatching {
                    context.contentResolver.insert(CallLog.Calls.CONTENT_URI, values)
                }.getOrNull() != null) inserted++ else failed++
        } }
        return result(inserted, skipped, failed)
    }

    internal fun parseVCards(text: String): List<VCard> {
        val unfolded = text.replace("\r\n", "\n").lines().fold(mutableListOf<String>()) { out, line ->
            if ((line.startsWith(' ') || line.startsWith('\t')) && out.isNotEmpty())
                out[out.lastIndex] += line.drop(1)
            else out += line
            out
        }
        val cards = mutableListOf<VCard>()
        var current: MutableList<String>? = null
        unfolded.forEach { line -> when {
            line.equals("BEGIN:VCARD", true) -> current = mutableListOf()
            line.equals("END:VCARD", true) -> current?.let { lines ->
                val name = value(lines.firstOrNull { it.substringBefore(':')
                    .substringBefore(';').equals("FN", true) }.orEmpty())
                val phones = lines.filter { it.substringBefore(':').substringBefore(';')
                    .equals("TEL", true) }.map(::value)
                val emails = lines.filter { it.substringBefore(':').substringBefore(';')
                    .equals("EMAIL", true) }.map(::value)
                cards += VCard(name, phones, emails)
                current = null
            }
            current != null && line.length <= 65_536 -> current?.add(line)
        } }
        return cards
    }

    private fun value(line: String): String = line.substringAfter(':', "")
        .replace("\\n", "\n", true).replace("\\,", ",").replace("\\;", ";")
        .replace("\\\\", "\\").trim().take(8_192)
    private fun normalizePhone(value: String?): String =
        PhoneNumberUtils.normalizeNumber(value.orEmpty()).take(64)
    private fun callKey(number: String?, date: Long, duration: Long, type: Int) =
        "${normalizePhone(number)}|$date|$duration|$type"
    private fun result(inserted: Int, skipped: Int, failed: Int) = JSONObject()
        .put("ok", failed == 0).put("status", if (failed == 0) "succeeded" else "partial")
        .put("inserted", inserted).put("skipped", skipped).put("failed", failed)
    private fun granted(context: Context, permission: String) =
        ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED
    internal data class VCard(
        val name: String,
        val phones: List<String>,
        val emails: List<String>
    )
}
