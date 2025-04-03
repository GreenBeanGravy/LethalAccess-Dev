using HarmonyLib;
using System.Collections;
using UnityEngine;

namespace LethalAccess.Patches
{
    [HarmonyPatch(typeof(ClipboardItem))]
    public class ClipboardItemPatches
    {
        // Patch for speaking the page when flipping pages
        [HarmonyPatch(nameof(ClipboardItem.ItemInteractLeftRight)), HarmonyPostfix]
        public static void ItemInteractLeftRightPostfix(ClipboardItem __instance)
        {
            SpeakPage(__instance.currentPage);
        }

        [HarmonyPatch(nameof(ClipboardItem.EquipItem)), HarmonyPostfix]
        public static void EquipItemPostfix(ClipboardItem __instance)
        {
            LACore.Instance.StartCoroutine(DelayedSpeakPage(__instance.currentPage));
        }

        private static IEnumerator DelayedSpeakPage(int currentPage)
        {
            yield return new WaitForSeconds(0.1f);
            SpeakPage(currentPage);
        }

        private static void SpeakPage(int currentPage)
        {
            string pageText = GetPageContent(currentPage);
            Utilities.SpeakText($"Page {currentPage}. {pageText}");
        }

        private static string GetPageContent(int currentPage)
        {
            switch (currentPage)
            {
                case 1:
                    return "An order number, 4186915, is prominently displayed at the upper edge. Below, a list of content categories is presented, indicating the inclusion of general descriptions, procedures for emergencies, diagrams both block and schematic, as well as detailed exploded views and a list of parts. Notably mentioned are the entities Halden Electronics and F. Power Co. A grid with numerous entries, indicative of a specifications chart, spans the central portion, but the text within is blurred, rendering the specifics indiscernible. A triangular warning icon attracts attention to a cautionary note below, which explicitly states that the provided information is tailored for authorized and skilled technicians, explicitly excluding the general public. It highlights an intentional omission of certain cautions or warnings, aimed at enhancing readability, while also clearly disclaiming liability for any harm or fatality that may result from misuse of the information or related tools. The lower segment features the logo of Halden Electronics, reinforcing the brand's presence. Accompanying this is a legal notice, asserting the exclusive trademark rights of Halden Electronics to the content, and strictly prohibiting the distribution of the material therein.";
                case 2:
                    return "At the top, a section number and title indicate instructions for operating an Echo Scanner, a patented device for employee use. Instructions suggest using a right mouse button (RMB) to activate the scanner towards objects of interest. Upon detection, the scanner provides data such as monetary value, name, and purpose, with color-coded information: green for places or objects of interest, yellow for objects returnable as valuable scrap to the Company, and red for biological matter such as wildlife.\r\n\r\nA tip box emphasizes using the scanner to locate the autopilot ship or other points of interest when outside, noting the scanner's signal range of up to 50 meters in open areas.\r\n\r\nAn illustration shows radio waves emanating from a human figure wearing a helmet, suggesting the operational range of the scanner.\r\n\r\nBelow, a warning icon prefaces a caution about the scanner's built-in components in helmet compartments emitting radiation, with a potential increase in cancer risk and other illnesses. The Company's obligation to disclose this information complies with the HDHAN Health Act.\r\n\r\nThe page is numbered 146 at the bottom.";
                case 3: 
                    return "A section title suggests information about the relationship between an autopilot ship, a terminal, and the user, described as a contracted worker provided with one of the company's vehicles as a home base and access to a multi-use Terminal. Instructions for routing to moons using the terminal's GPS feature are given, with a note that travel to distant moons may require Company Credits, the cost of which is determined by a risk and cost-benefit analysis department.\r\n\r\nA tip box advises that safer and closer moons are generally less costly or free, and suggests adherence to these areas as recommended by the risk analysis team for the duration of a contract.\r\n\r\nUnder a subheading about purchasing tools, the Terminal is mentioned as a gateway to the Company Store, where items, specifically under 30 pounds, can be bought in bulk, highlighting a Survival Kit as essential for beginners. The delivery process for purchased items involves their arrival via a transport vehicle on the chosen moon's surface, with a cautionary instruction not to miss the delivery.\r\n\r\nAnother subheading, Bestiary, explains that using the Echo Scanner on wildlife transmits information to a research team, which is then added to the user's terminal bestiary if not already documented.\r\n\r\nThe page concludes with the number 139 at the bottom. An image of a computer terminal with a screen and keyboard is also shown, suggesting the described Terminal.";
                case 4:
                    return "A section heading indicates guidelines for returning scrap and conducting transactions with the Company. It underlines the expectation of returning materials in exchange for Company Credits, despite the perceived luxury of the contract period. Directions are given to route to the Company Building on 71-Gordion to sell scrap.\r\n\r\nA warning box lists specific directives for selling scrap: to avoid loitering around the counter, prepare all scrap for bulk placement on the counter, and to signal the Company by ringing a bell until acknowledged, all while maintaining silence.\r\n\r\nA note highlights that the exchange rate from scrap to Credits is variable, advising to verify current rates via the terminal.\r\n\r\nAnother section heading introduces general and miscellaneous job tips. It advises using an electric coil for charging battery-powered items, planning trips considering that the autopilot ship won't stay on a moon surface past midnight, keeping a crewmate at \"home\" for intelligence and remote access capabilities, and using the terminal to broadcast special codes for accessing secure doors and areas, with \"E9\" provided as an example code.\r\n\r\nThe page number 140 is visible at the bottom.";
                default:
                    return "Unknown page.";
            }
        }
    }
}