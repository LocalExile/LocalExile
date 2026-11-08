// Pre-script for Ghidra's AutoVersionTrackingScript when two client builds are mapped (tooling.md section 11):
// the stock defaults without the duplicate-function correlator. On the 79 MB client that correlator adds every
// many-to-many pair of identical small functions and did not finish in over two hours; PortMap.java maps those
// functions through the aligned call sites instead, which only accepts unanimous ones.
//@category LocalExile
import ghidra.app.script.GhidraScript;
import ghidra.feature.vt.gui.util.VTOptionDefines;
import ghidra.features.base.values.GhidraValuesMap;

public class PortVtOptions extends GhidraScript {
    @Override
    public void run() throws Exception {
        GhidraValuesMap o = new GhidraValuesMap();
        o.defineBoolean(VTOptionDefines.CREATE_IMPLIED_MATCHES_OPTION_TEXT, true);
        o.defineBoolean(VTOptionDefines.RUN_EXACT_SYMBOL_OPTION_TEXT, true);
        o.defineBoolean(VTOptionDefines.RUN_EXACT_DATA_OPTION_TEXT, true);
        o.defineBoolean(VTOptionDefines.RUN_EXACT_FUNCTION_BYTES_OPTION_TEXT, true);
        o.defineBoolean(VTOptionDefines.RUN_EXACT_FUNCTION_INST_OPTION_TEXT, true);
        o.defineBoolean(VTOptionDefines.RUN_DUPE_FUNCTION_OPTION_TEXT, false);
        o.defineBoolean(VTOptionDefines.RUN_REF_CORRELATORS_OPTION_TEXT, true);
        o.defineInt(VTOptionDefines.DATA_CORRELATOR_MIN_LEN_OPTION_TEXT, 5);
        o.defineInt(VTOptionDefines.SYMBOL_CORRELATOR_MIN_LEN_OPTION_TEXT, 3);
        o.defineInt(VTOptionDefines.FUNCTION_CORRELATOR_MIN_LEN_OPTION_TEXT, 10);
        o.defineInt(VTOptionDefines.DUPE_FUNCTION_CORRELATOR_MIN_LEN_OPTION_TEXT, 10);
        o.defineBoolean(VTOptionDefines.APPLY_IMPLIED_MATCHES_OPTION_TEXT, true);
        o.defineInt(VTOptionDefines.MIN_VOTES_OPTION_TEXT, 2);
        o.defineInt(VTOptionDefines.MAX_CONFLICTS_OPTION_TEXT, 0);
        o.defineDouble(VTOptionDefines.REF_CORRELATOR_MIN_SCORE_OPTION_TEXT, 0.95);
        o.defineDouble(VTOptionDefines.REF_CORRELATOR_MIN_CONF_OPTION_TEXT, 10.0);
        state.addEnvironmentVar("autoVTOptionsMap", o);
    }
}
