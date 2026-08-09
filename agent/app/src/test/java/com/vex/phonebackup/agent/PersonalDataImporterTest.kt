package com.vex.phonebackup.agent

import org.junit.Assert.assertEquals
import org.junit.Test

class PersonalDataImporterTest {
    @Test
    fun `vcard parser unfolds lines and keeps contact fields`() {
        val cards = PersonalDataImporter.parseVCards(
            "BEGIN:VCARD\r\n" +
                "VERSION:3.0\r\n" +
                "FN:Alice\\, Example\r\n" +
                "TEL;TYPE=CELL:+1 555\r\n" +
                " 0100\r\n" +
                "EMAIL:Alice@Example.COM\r\n" +
                "END:VCARD\r\n"
        )

        assertEquals(1, cards.size)
        assertEquals("Alice, Example", cards.single().name)
        assertEquals(listOf("+1 5550100"), cards.single().phones)
        assertEquals(listOf("Alice@Example.COM"), cards.single().emails)
    }

    @Test
    fun `vcard parser ignores text outside complete cards`() {
        val cards = PersonalDataImporter.parseVCards(
            "FN:outside\nBEGIN:VCARD\nTEL:123\nEND:VCARD\nBEGIN:VCARD\nFN:unfinished"
        )

        assertEquals(1, cards.size)
        assertEquals(listOf("123"), cards.single().phones)
    }
}
