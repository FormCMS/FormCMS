
const searchParams = new URLSearchParams(window.location.search);
const schema = searchParams.get("schema");
let id = searchParams.get("id");
let editor ;

$(document).ready(function() {
    $("#entityActions").prop('hidden', schema !=="entity");
    $("#queryActions").prop('hidden', schema !=="query");
    $("#menuActions").prop('hidden', schema !=="menu");
    editor = loadEditor();
});

function loadEditor() {
    let editor = new JSONEditor($('#editor_holder')[0], {
        ajax: true,
        schema: {
            "$ref": `json/${schema}.json`,
        },
        compact: true,
        disable_collapse: true,
        disable_array_delete_last_row: true,
        disable_array_delete_all_rows: true,
        disable_properties: true,
        disable_edit_json: false,
        collapsed: true,
        object_layout: "grid",
        show_errors: 'always'
    });

    editor.on('ready',async function() {
        if (id) {
            $.LoadingOverlay("show");
            const {data, error} = await one(id);
            if (data){
                id = data.id;
                delete (data.id); //prevent json-edit add extra property
                editor.setValue(data);
            }else {
                $('#errorPanel').text(error).show();
            }
            $.LoadingOverlay("hide");
        } else {
            editor.setValue(null);
        }
    });

    editor.on('change', function() {
        let errors = editor.validate();
        let indicator = $('#valid_indicator');
        if (errors.length) {
            indicator.css('color', 'red').text("not valid");
        } else {
            indicator.css('color', 'green').text("valid");
        }
    });
    return editor;
}
async function submit(callback) {
    const errors = editor.validate();
    if (errors.length) {
        return;
    }

    const val = editor.getValue();
    val.type = schema;

    if (id) {
        val.id = +id;
    }

    $.LoadingOverlay("show");
    const {data, error} = await callback(val);
    if (data) {
        alert("submit succeed!");
        if (!id) {
            window.location.href = `edit.html?schema=${schema}&id=${data.name}`;
        }
        $('#errorPanel').text('').hide();
    } else {
        $('#errorPanel').text(error).show();
    }
    $.LoadingOverlay("hide");
}